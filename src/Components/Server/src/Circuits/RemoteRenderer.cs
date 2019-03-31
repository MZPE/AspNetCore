// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Microsoft.AspNetCore.Components.Browser.Rendering
{
    internal class RemoteRenderer : HtmlRenderer
    {
        internal const int MaxBatchSendAttempts = 3;
        internal static readonly TimeSpan SendMessageRetryInterval = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan SendMessageAcknowledgeInterval = TimeSpan.FromSeconds(1);

        private readonly IJSRuntime _jsRuntime;
        private readonly CircuitClientProxy _client;
        private readonly RendererRegistry _rendererRegistry;
        private readonly ILogger _logger;
        private long _nextRenderId = 1;
        private bool _disposing = false;

        /// <summary>
        /// Notifies when a rendering exception occured.
        /// </summary>
        public event EventHandler<Exception> UnhandledException;

        /// <summary>
        /// Creates a new <see cref="RemoteRenderer"/>.
        /// </summary>
        public RemoteRenderer(
            IServiceProvider serviceProvider,
            RendererRegistry rendererRegistry,
            IJSRuntime jsRuntime,
            CircuitClientProxy client,
            IDispatcher dispatcher,
            HtmlEncoder encoder,
            ILogger logger)
            : base(serviceProvider, encoder.Encode, dispatcher)
        {
            _rendererRegistry = rendererRegistry;
            _jsRuntime = jsRuntime;
            _client = client;

            Id = _rendererRegistry.Add(this);
            _logger = logger;
        }

        internal ConcurrentQueue<PendingRender> PendingRenderBatches = new ConcurrentQueue<PendingRender>();

        public int Id { get; }

        /// <summary>
        /// Associates the <see cref="IComponent"/> with the <see cref="RemoteRenderer"/>,
        /// causing it to be displayed in the specified DOM element.
        /// </summary>
        /// <param name="componentType">The type of the component.</param>
        /// <param name="domElementSelector">A CSS selector that uniquely identifies a DOM element.</param>
        public Task AddComponentAsync(Type componentType, string domElementSelector)
        {
            var component = InstantiateComponent(componentType);
            var componentId = AssignRootComponentId(component);

            var attachComponentTask = _jsRuntime.InvokeAsync<object>(
                "Blazor._internal.attachRootComponentToElement",
                Id,
                domElementSelector,
                componentId);
            CaptureAsyncExceptions(attachComponentTask);

            return RenderRootComponentAsync(componentId);
        }

        /// <inheritdoc />
        protected override void HandleException(Exception exception)
        {
            if (exception is AggregateException aggregateException)
            {
                foreach (var innerException in aggregateException.Flatten().InnerExceptions)
                {
                    Log.UnhandledExceptionRenderingComponent(_logger, innerException);
                }
            }
            else
            {
                Log.UnhandledExceptionRenderingComponent(_logger, exception);
            }

            UnhandledException?.Invoke(this, exception);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            _disposing = true;
            base.Dispose(true);
            while (PendingRenderBatches.TryDequeue(out var entry))
            {
                entry.CompletionSource.TrySetCanceled();
            }
            _rendererRegistry.TryRemove(Id);
        }

        /// <inheritdoc />
        protected override Task UpdateDisplayAsync(in RenderBatch batch)
        {
            if (_disposing)
            {
                // We are being disposed, so do no work.
                return Task.FromCanceled<object>(CancellationToken.None);
            }

            // Note that we have to capture the data as a byte[] synchronously here, because
            // SignalR's SendAsync can wait an arbitrary duration before serializing the params.
            // The RenderBatch buffer will get reused by subsequent renders, so we need to
            // snapshot its contents now.
            // TODO: Consider using some kind of array pool instead of allocating a new
            //       buffer on every render.
            var batchBytes = MessagePackSerializer.Serialize(batch, RenderBatchFormatterResolver.Instance);

            var renderId = Interlocked.Increment(ref _nextRenderId);

            var pendingRender = new PendingRender(
                renderId,
                batchBytes,
                new TaskCompletionSource<object>());

            // Buffer the rendered batches no matter what. We'll send it down immediately when the client
            // is connected or right after the client reconnects.

            PendingRenderBatches.Enqueue(pendingRender);

            // Fire and forget the initial send for this batch (if connected). Otherwise it will be sent
            // as soon as the client reconnects.
            var _ = WriteBatchBytesAsync(pendingRender);

            return pendingRender.CompletionSource.Task;
        }

        public Task ProcessBufferedRenderBatches()
        {
            // Upon reconnection we send all the pending batches (we'll let the client sort the order out) 
            return Task.WhenAll(PendingRenderBatches.Select(b => ErrorHandledWrite(WriteBatchBytesAsync(b))));

            async Task ErrorHandledWrite(Task batch)
            {
                try
                {
                    await batch;
                }
                catch (Exception e)
                {
                    HandleException(e);
                }
            }
        }

        private async Task WriteBatchBytesAsync(PendingRender pending)
        {
            // Send the render batch to the client
            // If the "send" operation fails (synchronously or asynchronously) or the client
            // gets disconected retry three times and then give up. This likely mean that
            // the circuit went offline while sending the data, so simply wait until the
            // client reconnects back or the circuit gets evicted because it stayed
            // disconnected for too long.

            var currentAttempt = 0;
            do
            {
                try
                {
                    if (!_client.Connected)
                    {
                        // If we detect that the client is offline. Simply stop trying to send the payload.
                        // When the client reconnects we'll resend it.
                        return;
                    }

                    if (!PendingRenderBatches.TryPeek(out var next) || pending.BatchId < next.BatchId)
                    {
                        // The client has ack this or a later batch already.
                        return;
                    }

                    Log.BeginUpdateDisplayAsync(_logger, _client.ConnectionId);
                    await _client.SendAsync("JS.RenderBatch", Id, pending.BatchId, pending.Data);
                    await Task.Delay(SendMessageAcknowledgeInterval);
                }
                catch (Exception e)
                {
                    Log.SendBatchDataFailed(_logger, e);
                    // Wait 10 seconds after we tried to send the payload, then check that that the client is still connected and
                    // that we haven't received any ack from the client in the mean-time to retry.
                    await Task.Delay(SendMessageRetryInterval);
                }

                currentAttempt++;
            } while (currentAttempt < MaxBatchSendAttempts);

            // We don't have to remove the entry from the list of pending batches if we fail to send it or the client fails to
            // acknowledge that it received it. We simply keep it in the queue until we receive another ack from the client for
            // a later batch (clientBatchId > thisBatchId) or the circuit becomes disconnected and we ultimately get evicted and
            // disposed.

            if (_client.Connected &&
                PendingRenderBatches.TryPeek(out var current) &&
                pending.BatchId >= current.BatchId)
            {
                // If we are still connected after three attempts to send a given batch then we need to fail the rendering process.
                HandleException(new InvalidOperationException("Max number of attempts to send a batch reached while the client was connected."));
            }
        }

        public void OnRenderCompleted(long incomingBatchId, string errorMessageOrNull)
        {
            if (_disposing)
            {
                // Disposing so don't do work.
                return;
            }

            // We peek first in case we are receiving a duplicate or out of order ack from
            // the client.
            while (PendingRenderBatches.TryPeek(out var entry))
            {
                // Dequeue entries until we sync with the render batch number received.
                if (incomingBatchId < entry.BatchId)
                {
                    _logger.LogInformation($"Incoming batch {incomingBatchId} already acknowledged.");
                    // We successfully received an ack for a pending batch left.
                    // Which we already processed so no work.
                    break;
                }

                // batchId >= entry.BatchId -> We are in sync or we missed an ack from the client.
                // We simply catch up as a bigger number means the client already rendered
                // previous batches successfully.
                if (incomingBatchId >= entry.BatchId)
                {
                    _logger.LogInformation($"Acknowledging batch {entry.BatchId} ack sequence {incomingBatchId}.");
                    // When the render is completed (success, fail), stop tracking it.
                    // We are the only ones removing things from the queue so Peek+Dequeue is ok here.
                    PendingRenderBatches.TryDequeue(out var _);

                    // If we are catching up, then invoke completed renders in the absence of errors.
                    if ((errorMessageOrNull == null && entry.BatchId < incomingBatchId))
                    {
                        _logger.LogDebug($"Completing batch {entry.BatchId} without error.");
                        CompleteRender(entry.CompletionSource, errorMessageOrNull);
                    }

                    if (entry.BatchId == incomingBatchId)
                    {
                        string message = $"Completing batch {entry.BatchId} " +
                            errorMessageOrNull == null ? "without error." : "with error.";

                        _logger.LogDebug(message);
                        CompleteRender(entry.CompletionSource, errorMessageOrNull);

                        // We cought up.
                        break;
                    }
                }
            }
        }

        private void CompleteRender(TaskCompletionSource<object> pendingRenderInfo, string errorMessageOrNull)
        {
            if (errorMessageOrNull == null)
            {
                pendingRenderInfo.TrySetResult(null);
            }
            else
            {
                pendingRenderInfo.TrySetException(new RemoteRendererException(errorMessageOrNull));
            }
        }

        internal readonly struct PendingRender
        {
            public PendingRender(long batchId, byte[] data, TaskCompletionSource<object> completionSource)
            {
                BatchId = batchId;
                Data = data;
                CompletionSource = completionSource;
            }

            public long BatchId { get; }
            public byte[] Data { get; }
            public TaskCompletionSource<object> CompletionSource { get; }
        }

        private void CaptureAsyncExceptions(Task task)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    UnhandledException?.Invoke(this, t.Exception);
                }
            });
        }

        private static class Log
        {
            private static readonly Action<ILogger, string, Exception> _unhandledExceptionRenderingComponent;
            private static readonly Action<ILogger, string, Exception> _beginUpdateDisplayAsync;
            private static readonly Action<ILogger, string, Exception> _bufferingRenderDisconnectedClient;
            private static readonly Action<ILogger, string, Exception> _sendBatchDataFailed;

            private static class EventIds
            {
                public static readonly EventId UnhandledExceptionRenderingComponent = new EventId(100, "ExceptionRenderingComponent");
                public static readonly EventId BeginUpdateDisplayAsync = new EventId(101, "BeginUpdateDisplayAsync");
                public static readonly EventId SkipUpdateDisplayAsync = new EventId(102, "SkipUpdateDisplayAsync");
                public static readonly EventId SendBatchDataFailed = new EventId(103, "SendBatchDataFailed");
            }

            static Log()
            {
                _unhandledExceptionRenderingComponent = LoggerMessage.Define<string>(
                    LogLevel.Warning,
                    EventIds.UnhandledExceptionRenderingComponent,
                    "Unhandled exception rendering component: {Message}");

                _beginUpdateDisplayAsync = LoggerMessage.Define<string>(
                    LogLevel.Trace,
                    EventIds.BeginUpdateDisplayAsync,
                    "Begin remote rendering of components on client {ConnectionId}.");

                _bufferingRenderDisconnectedClient = LoggerMessage.Define<string>(
                    LogLevel.Trace,
                    EventIds.SkipUpdateDisplayAsync,
                    "Buffering remote render because the client on connection {ConnectionId} is disconnected.");

                _sendBatchDataFailed = LoggerMessage.Define<string>(
                    LogLevel.Information,
                    EventIds.SendBatchDataFailed,
                    "Sending data for batch failed: {Message}");
            }

            public static void SendBatchDataFailed(ILogger logger, Exception exception)
            {
                _sendBatchDataFailed(logger, exception.Message, exception);
            }

            public static void UnhandledExceptionRenderingComponent(ILogger logger, Exception exception)
            {
                _unhandledExceptionRenderingComponent(
                    logger,
                    exception.Message,
                    exception);
            }

            public static void BeginUpdateDisplayAsync(ILogger logger, string connectionId)
            {
                _beginUpdateDisplayAsync(
                    logger,
                    connectionId,
                    null);
            }

            public static void BufferingRenderDisconnectedClient(ILogger logger, string connectionId)
            {
                _bufferingRenderDisconnectedClient(
                    logger,
                    connectionId,
                    null);
            }
        }
    }
}
