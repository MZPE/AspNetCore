// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace Microsoft.AspNetCore.Components.Browser.Rendering
{
    public class RemoteRendererTest : HtmlRendererTestBase
    {
        protected override HtmlRenderer GetHtmlRenderer(IServiceProvider serviceProvider)
        {
            return GetRemoteRenderer(serviceProvider, CircuitClientProxy.CreateOffline());
        }

        [Fact]
        public void WritesAreBufferedWhenTheClientIsOffline()
        {
            // Arrange
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var renderer = (RemoteRenderer)GetHtmlRenderer(serviceProvider);
            var component = new TestComponent(builder =>
            {
                builder.OpenElement(0, "my element");
                builder.AddContent(1, "some text");
                builder.CloseElement();
            });

            // Act
            var componentId = renderer.AssignRootComponentId(component);
            component.TriggerRender();
            component.TriggerRender();

            // Assert
            Assert.Equal(2, renderer.PendingRenderBatches.Count);
        }

        [Fact]
        public async Task ProcessBufferedRenderBatches_WritesRenders()
        {
            // Arrange
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var renderIds = new List<long>();

            var firstBatchTCS = new TaskCompletionSource<object>();
            var secondBatchTCS = new TaskCompletionSource<object>();
            var thirdBatchTCS = new TaskCompletionSource<object>();

            var initialClient = new Mock<IClientProxy>();
            initialClient.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback((string name, object[] value, CancellationToken token) =>
                {
                    renderIds.Add((long)value[1]);
                })
                .Returns(firstBatchTCS.Task);
            var circuitClient = new CircuitClientProxy(initialClient.Object, "connection0");
            var renderer = GetRemoteRenderer(serviceProvider, circuitClient);
            var component = new TestComponent(builder =>
            {
                builder.OpenElement(0, "my element");
                builder.AddContent(1, "some text");
                builder.CloseElement();
            });

            var client = new Mock<IClientProxy>();
            client.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback((string name, object[] value, CancellationToken token) =>
                {
                    renderIds.Add((long)value[1]);
                })
                .Returns<string, object[], CancellationToken>((n, v, t) => (long)v[1] == 3 ? secondBatchTCS.Task : thirdBatchTCS.Task);

            var componentId = renderer.AssignRootComponentId(component);
            component.TriggerRender();
            renderer.OnRenderCompleted(2, null);
            firstBatchTCS.SetResult(null);

            circuitClient.SetDisconnected();
            component.TriggerRender();
            component.TriggerRender();

            // Act
            circuitClient.Transfer(client.Object, "new-connection");
            var task = renderer.ProcessBufferedRenderBatches();

            foreach (var id in renderIds.ToArray())
            {
                renderer.OnRenderCompleted(id, null);
            }

            secondBatchTCS.SetResult(null);
            thirdBatchTCS.SetResult(null);

            // Assert
            Assert.Equal(new long[] { 2, 3, 4 }, renderIds);
            Assert.True(task.Wait(3000), "One or more render batches werent acknowledged");

            await task;
        }

        [Fact]
        public async Task OnRenderCompletedAsync_IgnoresAlreadyProcessedAcks()
        {
            // Arrange
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var renderIds = new List<long>();

            var firstBatchTCS = new TaskCompletionSource<object>();
            var secondBatchTCS = new TaskCompletionSource<object>();
            var thirdBatchTCS = new TaskCompletionSource<object>();

            var initialClient = new Mock<IClientProxy>();
            initialClient.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback((string name, object[] value, CancellationToken token) =>
                {
                    renderIds.Add((long)value[1]);
                })
                .Returns(firstBatchTCS.Task);
            var circuitClient = new CircuitClientProxy(initialClient.Object, "connection0");
            var renderer = GetRemoteRenderer(serviceProvider, circuitClient);
            var component = new TestComponent(builder =>
            {
                builder.OpenElement(0, "my element");
                builder.AddContent(1, "some text");
                builder.CloseElement();
            });

            var client = new Mock<IClientProxy>();
            client.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback((string name, object[] value, CancellationToken token) =>
                {
                    renderIds.Add((long)value[1]);
                })
                .Returns<string, object[], CancellationToken>((n, v, t) => (long)v[1] == 3 ? secondBatchTCS.Task : thirdBatchTCS.Task);

            var componentId = renderer.AssignRootComponentId(component);
            component.TriggerRender();
            renderer.OnRenderCompleted(2, null);
            firstBatchTCS.SetResult(null);

            circuitClient.SetDisconnected();
            component.TriggerRender();
            component.TriggerRender();

            // Act
            circuitClient.Transfer(client.Object, "new-connection");
            var task = renderer.ProcessBufferedRenderBatches();

            renderer.OnRenderCompleted(3, null);
            secondBatchTCS.SetResult(null);

            renderer.OnRenderCompleted(2, null);

            // Assert
            Assert.Equal(new long[] { 2, 3, 4 }, renderIds);
            var pendingBatch = Assert.Single(renderer.PendingRenderBatches);
            Assert.Equal(4, pendingBatch.BatchId);
            renderer.OnRenderCompleted(4, null);
            thirdBatchTCS.SetResult(null);
            Assert.Empty(renderer.PendingRenderBatches);

            Assert.True(task.Wait(3000), "One or more render batches werent acknowledged");

            await task;
        }

        [Fact]
        public async Task GracefullyRecoversFromMissingClientAcknowledge()
        {
            // Arrange
            var serviceProvider = new ServiceCollection().BuildServiceProvider();

            var firstBatchTCS = new TaskCompletionSource<object>();
            var secondBatchTCS = new TaskCompletionSource<object>();

            var offlineClient = new CircuitClientProxy(new Mock<IClientProxy>(MockBehavior.Strict).Object, "offline-client");
            offlineClient.SetDisconnected();
            var renderer = GetRemoteRenderer(serviceProvider, offlineClient);

            RenderFragment initialContent = (builder) =>
            {
                builder.OpenElement(0, "my element");
                builder.AddContent(1, "some text");
                builder.CloseElement();
            };
            var trigger = new Trigger();

            var renderIds = new List<long>();
            var onlineClient = new Mock<IClientProxy>();
            onlineClient.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback((string name, object[] value, CancellationToken token) =>
                {
                    renderIds.Add((long)value[1]);
                })
                .Returns<string, object[], CancellationToken>((n, v, t) => (long)v[1] == 2 ? firstBatchTCS.Task : secondBatchTCS.Task);

            // This produces the initial batch (id = 2)
            var result = await renderer.RenderComponentAsync<AutoParameterTestComponent>(
                ParameterCollection.FromDictionary(new Dictionary<string, object>
                {
                    [nameof(AutoParameterTestComponent.Content)] = initialContent,
                    [nameof(AutoParameterTestComponent.Trigger)] = trigger
                }));

            trigger.Component.Content = (builder) =>
            {
                builder.OpenElement(0, "offline element");
                builder.AddContent(1, "offline text");
                builder.CloseElement();
            };

            // This produces an additional batch (id = 3)
            trigger.TriggerRender();

            var originallyQueuedBatches = renderer.PendingRenderBatches.Count;

            // Act            
            offlineClient.Transfer(onlineClient.Object, "new-connection");
            var task = renderer.ProcessBufferedRenderBatches();
            // Pretend that we missed the ack for the initial batch
            renderer.OnRenderCompleted(3, null);
            firstBatchTCS.SetResult(null);
            secondBatchTCS.SetResult(null);

            // Assert
            Assert.Equal(2, originallyQueuedBatches);
            renderIds.Sort();
            Assert.Equal(new long[] { 2, 3 }, renderIds);
            Assert.True(task.Wait(5000), "One or more render batches werent acknowledged");

            Assert.Empty(renderer.PendingRenderBatches);
        }

        [Fact]
        public async Task StopsRenderingIfClientDoesNotAcknowledgeRenders()
        {
            // Arrange
            var serviceProvider = new ServiceCollection().BuildServiceProvider();

            var firstAttemptTCS = new TaskCompletionSource<object>();
            var secondAttemptTCS = new TaskCompletionSource<object>();
            var thirdAttemptTCS = new TaskCompletionSource<object>();

            var renderIds = new List<long>();
            var onlineClient = new Mock<IClientProxy>();

            onlineClient.SetupSequence(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(firstAttemptTCS.Task)
                .Returns(secondAttemptTCS.Task)
                .Returns(thirdAttemptTCS.Task);

            var renderer = GetRemoteRenderer(serviceProvider, new CircuitClientProxy(onlineClient.Object, "missbehaving-client"));

            var initialRender = renderer.AddComponentAsync(typeof(TestComponent), "tc");
            var pendingBatch = Assert.Single(renderer.PendingRenderBatches.ToArray());
            var exceptions = new List<Exception>();
            void CaptureExceptions(object sender, Exception exception) => exceptions.Add(exception);
            renderer.UnhandledException += CaptureExceptions;

            // Act
            firstAttemptTCS.SetResult(0);
            secondAttemptTCS.SetResult(0);
            thirdAttemptTCS.SetResult(0);

            await Task.Delay(RemoteRenderer.SendMessageRetryInterval * RemoteRenderer.MaxBatchSendAttempts);

            // Assert
            renderer.UnhandledException -= CaptureExceptions;
            Assert.Single(exceptions);
            await initialRender;
        }

        [Fact]
        public async Task RecoversIfItMissesARenderInBetweenASequenceAsync()
        {
            // Arrange
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var renderIds = new List<long>();

            var firstBatchTCS = new TaskCompletionSource<object>();
            var secondBatchTCS = new TaskCompletionSource<object>();
            var thirdBatchTCS = new TaskCompletionSource<object>();

            var initialClient = new Mock<IClientProxy>();
            initialClient.SetupSequence(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(firstBatchTCS.Task)
                .Returns(secondBatchTCS.Task)
                .Returns(thirdBatchTCS.Task);

            var circuitClient = new CircuitClientProxy(initialClient.Object, "connection0");
            var renderer = GetRemoteRenderer(serviceProvider, circuitClient);
            var component = new TestComponent(builder =>
            {
                builder.OpenElement(0, "my element");
                builder.AddContent(1, "some text");
                builder.CloseElement();
            });

            var componentId = renderer.AssignRootComponentId(component);
            component.TriggerRender();
            renderer.OnRenderCompleted(2, null);
            firstBatchTCS.SetResult(null);

            component.TriggerRender();
            component.TriggerRender();

            var exceptions = new List<Exception>();
            void CaptureExceptions(object sender, Exception exception) => exceptions.Add(exception);
            renderer.UnhandledException += CaptureExceptions;

            // Act
            secondBatchTCS.SetException(new InvalidOperationException("Failed to send payload."));
            renderer.OnRenderCompleted(4, null);
            thirdBatchTCS.SetResult(null);
            await Task.Delay(RemoteRenderer.SendMessageRetryInterval);

            // Assert
            Assert.Empty(exceptions);
        }

        [Fact]
        public async Task PrerendersMultipleComponentsSuccessfully()
        {
            // Arrange
            var serviceProvider = new ServiceCollection().BuildServiceProvider();

            var renderer = GetRemoteRenderer(
                serviceProvider,
                CircuitClientProxy.CreateOffline());

            // Act
            var first = await renderer.RenderComponentAsync<TestComponent>(ParameterCollection.Empty);
            var second = await renderer.RenderComponentAsync<TestComponent>(ParameterCollection.Empty);

            // Assert
            Assert.Equal(0, first.ComponentId);
            Assert.Equal(1, second.ComponentId);
            Assert.Equal(2, renderer.PendingRenderBatches.Count);
        }

        [Fact]
        public async Task DisconectingWhileSendingABatchDoesNotCauseARenderFailure()
        {
            // Arrange
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var renderIds = new List<long>();

            var firstBatchTCS = new TaskCompletionSource<object>();
            var secondBatchTCS = new TaskCompletionSource<object>();
            var thirdBatchTCS = new TaskCompletionSource<object>();

            var initialClient = new Mock<IClientProxy>();
            initialClient.SetupSequence(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(firstBatchTCS.Task)
                .Returns(secondBatchTCS.Task)
                .Returns(thirdBatchTCS.Task);

            var circuitClient = new CircuitClientProxy(initialClient.Object, "connection0");
            var renderer = GetRemoteRenderer(serviceProvider, circuitClient);
            var component = new TestComponent(builder =>
            {
                builder.OpenElement(0, "my element");
                builder.AddContent(1, "some text");
                builder.CloseElement();
            });

            var componentId = renderer.AssignRootComponentId(component);
            component.TriggerRender();
            renderer.OnRenderCompleted(2, null);
            firstBatchTCS.SetResult(null);

            component.TriggerRender();
            component.TriggerRender();

            var exceptions = new List<Exception>();
            void CaptureExceptions(object sender, Exception exception) => exceptions.Add(exception);
            renderer.UnhandledException += CaptureExceptions;

            // Act
            circuitClient.SetDisconnected();
            secondBatchTCS.SetException(new InvalidOperationException("Failed to send payload."));
            thirdBatchTCS.SetException(new InvalidOperationException("Failed to send payload."));
            await Task.Delay(RemoteRenderer.SendMessageRetryInterval);

            // Assert
            Assert.Empty(exceptions);
            Assert.Equal(2, renderer.PendingRenderBatches.Count);
        }

        private RemoteRenderer GetRemoteRenderer(IServiceProvider serviceProvider, CircuitClientProxy circuitClientProxy)
        {
            var jsRuntime = new Mock<IJSRuntime>();
            jsRuntime.Setup(r => r.InvokeAsync<object>(
                "Blazor._internal.attachRootComponentToElement",
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<int>()))
                .ReturnsAsync(Task.FromResult<object>(null));

            return new RemoteRenderer(
                serviceProvider,
                new RendererRegistry(),
                jsRuntime.Object,
                circuitClientProxy,
                Dispatcher,
                HtmlEncoder.Default,
                NullLogger.Instance);
        }

        private class TestComponent : IComponent
        {
            private RenderHandle _renderHandle;
            private RenderFragment _renderFragment = (builder) =>
            {
                builder.OpenElement(0, "my element");
                builder.AddContent(1, "some text");
                builder.CloseElement();
            };

            public TestComponent()
            {
            }

            public TestComponent(RenderFragment renderFragment)
            {
                _renderFragment = renderFragment;
            }

            public void Configure(RenderHandle renderHandle)
            {
                _renderHandle = renderHandle;
            }

            public Task SetParametersAsync(ParameterCollection parameters)
            {
                TriggerRender();
                return Task.CompletedTask;
            }

            public void TriggerRender()
            {
                var task = _renderHandle.Invoke(() => _renderHandle.Render(_renderFragment));
                Assert.True(task.IsCompletedSuccessfully);
            }
        }

        private class AutoParameterTestComponent : IComponent
        {
            private RenderHandle _renderHandle;

            [Parameter] public RenderFragment Content { get; set; }

            [Parameter] public Trigger Trigger { get; set; }

            public void Configure(RenderHandle renderHandle)
            {
                _renderHandle = renderHandle;
            }

            public Task SetParametersAsync(ParameterCollection parameters)
            {
                Content = parameters.GetValueOrDefault<RenderFragment>(nameof(Content));
                Trigger ??= parameters.GetValueOrDefault<Trigger>(nameof(Trigger));
                Trigger.Component = this;
                TriggerRender();
                return Task.CompletedTask;
            }

            public void TriggerRender()
            {
                var task = _renderHandle.Invoke(() => _renderHandle.Render(Content));
                Assert.True(task.IsCompletedSuccessfully);
            }
        }

        private class Trigger
        {
            public AutoParameterTestComponent Component { get; set; }
            public void TriggerRender()
            {
                Component.TriggerRender();
            }
        }
    }
}
