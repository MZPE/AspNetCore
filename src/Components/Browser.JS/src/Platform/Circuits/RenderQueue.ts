import { renderBatch } from '../../Rendering/Renderer';
import { OutOfProcessRenderBatch } from '../../Rendering/RenderBatch/OutOfProcessRenderBatch';
import { ILogger, LogLevel } from '../Logging/ILogger';

export enum BatchStatus {
  Pending = 1,
  Processed = 2,
  Queued = 3
}

export default class RenderQueue {
  private static renderQueues = new Map<number, RenderQueue>();

  private pendingRenders = new Map<number, Uint8Array>();
  private nextBatchId = 2;
  public browserRendererId: number;
  public logger: ILogger;

  public constructor(browserRendererId: number, logger: ILogger) {
    this.browserRendererId = browserRendererId;
    this.logger = logger;
  }

  public static getOrCreateQueue(browserRendererId: number, logger: ILogger): RenderQueue {
    const queue = this.renderQueues.get(browserRendererId);
    if (queue) {
      return queue;
    }

    const newQueue = new RenderQueue(browserRendererId, logger);
    this.renderQueues.set(browserRendererId, newQueue);
    return newQueue;
  }

  public enqueue(receivedBatchId: number, receivedBatchData: Uint8Array): BatchStatus {
    if (receivedBatchId < this.nextBatchId) {
      this.logger.log(LogLevel.Debug, `Batch ${receivedBatchId} already processed. Waiting for batch ${this.nextBatchId}.`);
      return BatchStatus.Processed;
    }

    if (this.pendingRenders.has(receivedBatchId)) {
      this.logger.log(LogLevel.Debug, `Batch ${receivedBatchId} already queued. Waiting for batch ${this.nextBatchId}.`);
      return BatchStatus.Pending;
    }

    this.logger.log(LogLevel.Debug, `Batch ${receivedBatchId} successfully queued.`);
    this.pendingRenders.set(receivedBatchId, receivedBatchData);
    return BatchStatus.Queued;
  }

  public renderPendingBatches(connection: signalR.HubConnection): void {
    let batchId: number | undefined;
    let batchData: Uint8Array | undefined;

    try {
      let next = this.tryDequeueNextBatch();
      batchId = next.batchId;
      batchData = next.batchData;
      while (batchId && batchData) {
        this.logger.log(LogLevel.Information, `Applying batch ${batchId}.`);
        renderBatch(this.browserRendererId, new OutOfProcessRenderBatch(batchData));
        this.completeBatch(connection, batchId);

        next = this.tryDequeueNextBatch();
        batchId = next.batchId;
        batchData = next.batchData;
      }
    } catch (ex) {
      this.logger.log(LogLevel.Error, `There was an error applying batch ${batchId}.`);

      // If there's a rendering exception, notify server *and* throw on client
      connection.send('OnRenderCompleted', batchId, ex.toString());
      throw ex;
    }
  }

  private tryDequeueNextBatch(): { batchId?: number; batchData?: Uint8Array } {
    const batchId = this.nextBatchId;
    const batchData = this.pendingRenders.get(this.nextBatchId);
    if (batchData != undefined) {
      this.dequeueBatch();
      return { batchId, batchData };
    } else {
      return {};
    }
  }

  public getLastBatchid(): number {
    return this.nextBatchId - 1;
  }

  private dequeueBatch(): void {
    this.pendingRenders.delete(this.nextBatchId);
    this.nextBatchId++;
  }

  private async completeBatch(connection: signalR.HubConnection, batchId: number): Promise<void> {
    for (let i = 0; i < 3; i++) {
      try {
        await connection.send('OnRenderCompleted', batchId, null);
      }
      catch {
        this.logger.log(LogLevel.Warning, `Failed to deliver completion notification for render '${batchId}' on attempt '${i}'.`);
      }
    }
  }
}
