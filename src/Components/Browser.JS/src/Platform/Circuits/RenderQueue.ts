import { renderBatch } from "../../Rendering/Renderer";
import { OutOfProcessRenderBatch } from "../../Rendering/RenderBatch/OutOfProcessRenderBatch";

export enum BatchStatus {
  Pending = 1,
  Processed = 2,
  Queued = 3
}

export default class RenderQueue {
  private static renderQueues = new Map<number, RenderQueue>();

  private pendingRenders = new Map<number, Uint8Array>();
  private nextRenderId = 2;

  constructor(public browserRendererId) { }

  static getOrCreateQueue(browserRendererId: number) {
    const queue = this.renderQueues.get(browserRendererId);
    if (!!queue) {
      return queue;
    }

    const newQueue = new RenderQueue(browserRendererId);
    this.renderQueues.set(browserRendererId, newQueue);
    return newQueue;
  }

  public enqueue(receivedBatchId, receivedBatchData) {
    if (receivedBatchId < this.nextRenderId) {
      return BatchStatus.Processed;
    }

    if (this.pendingRenders.has(receivedBatchId)) {
      return BatchStatus.Pending;
    }

    this.pendingRenders.set(receivedBatchId, receivedBatchData);
    return BatchStatus.Queued;
  }

  public renderPendingBatches(connection: signalR.HubConnection) {
    let { batchId, batchData } = this.tryDequeueNextBatch();
    try {
      while (batchId && batchData) {
        renderBatch(this.browserRendererId, new OutOfProcessRenderBatch(batchData));
        this.completeBatch(connection, batchId);

        const next = this.tryDequeueNextBatch();
        batchId = next.batchId;
        batchData = next.batchData;
      }
    } catch (ex) {
      // If there's a rendering exception, notify server *and* throw on client
      connection.send('OnRenderCompleted', batchId, ex.toString());
      throw ex;
    }
  }

  private tryDequeueNextBatch() {
    const batchId = this.nextRenderId;
    const batchData = this.pendingRenders.get(this.nextRenderId);
    if (batchData != undefined) {
      this.dequeueBatch();
      return { batchId, batchData };
    } else {
      return {};
    }
  }

  public getLastBatchid() {
    return this.nextRenderId - 1;
  }

  private dequeueBatch() {
    this.pendingRenders.delete(this.nextRenderId);
    this.nextRenderId++;
  }

  private async completeBatch(connection: signalR.HubConnection, batchId: number) {
    for (let i = 0; i < 3; i++) {
      try {
        await connection.send('OnRenderCompleted', batchId, null);
      }
      catch {
        console.log(`Failed to deliver completion notification for render '${batchId}' on attempt '${i}'.`);
      }
    }
  }
}
