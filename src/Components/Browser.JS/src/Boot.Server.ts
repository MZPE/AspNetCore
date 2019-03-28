import '@dotnet/jsinterop';
import './GlobalExports';
import * as signalR from '@aspnet/signalr';
import { MessagePackHubProtocol } from '@aspnet/signalr-protocol-msgpack';
import { OutOfProcessRenderBatch } from './Rendering/RenderBatch/OutOfProcessRenderBatch';
import { internalFunctions as uriHelperFunctions } from './Services/UriHelper';
import { renderBatch } from './Rendering/Renderer';
import { fetchBootConfigAsync, loadEmbeddedResourcesAsync } from './BootCommon';
import { CircuitHandler } from './Platform/Circuits/CircuitHandler';
import { AutoReconnectCircuitHandler } from './Platform/Circuits/AutoReconnectCircuitHandler';
import { attachRootComponentToElement } from './Rendering/Renderer';

async function boot() {
  const circuitHandlers: CircuitHandler[] = [new AutoReconnectCircuitHandler()];
  window['Blazor'].circuitHandlers = circuitHandlers;

  // In the background, start loading the boot config and any embedded resources
  const embeddedResourcesPromise = fetchBootConfigAsync().then(bootConfig => {
    return loadEmbeddedResourcesAsync(bootConfig);
  });

  const initialConnection = await initializeConnection(circuitHandlers);

  var circuitIds: string[] = [];
  var prerenderedCircuits = document.querySelectorAll("[data-component-id][data-circuit-id][data-renderer-id]");
  for (let i = 0; i < prerenderedCircuits.length; i++) {
    const element = prerenderedCircuits[i] as HTMLElement;
    const { componentId, circuitId, rendererId } = element.dataset;
    if (circuitIds.indexOf(circuitId!) === -1) {
      circuitIds.push(circuitId!);
    }

    for (let i = 0; i < circuitIds.length; i++) {
      const id = circuitIds[i];
      console.log(`Discovered circuit ${id}`);
    }
    const selector = `[data-component-id="${componentId}"][data-circuit-id="${circuitId}"][data-renderer-id="${rendererId}"]`;
    attachRootComponentToElement(Number.parseInt(rendererId!), selector, Number.parseInt(componentId!));
  }


  // Ensure any embedded resources have been loaded before starting the app
  await embeddedResourcesPromise;
  const circuitId = await initialConnection.invoke<string>(
    'StartCircuit',
    uriHelperFunctions.getLocationHref(),
    uriHelperFunctions.getBaseURI()
  );
  if(!circuitId){
    console.log(`No preregistered components to render.`);
  }

  const reconnect = async () => {
    const reconnection = await initializeConnection(circuitHandlers);
    var results = await Promise.all(circuitIds.map(id => reconnection.invoke<boolean>('ConnectCircuit', id)))

    if (!results.reduce((current, next) => current && next, true)) {
      return false;
    }

    circuitHandlers.forEach(h => h.onConnectionUp && h.onConnectionUp());
    return true;
  };

  window['Blazor'].reconnect = reconnect;

  const reconnectTask = reconnect();

  if (!!circuitId) {
    circuitIds.push(circuitId);
  }

  await reconnectTask;
}

async function initializeConnection(circuitHandlers: CircuitHandler[]): Promise<signalR.HubConnection> {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('_blazor')
    .withHubProtocol(new MessagePackHubProtocol())
    .configureLogging(signalR.LogLevel.Information)
    .build();

  connection.on('JS.BeginInvokeJS', DotNet.jsCallDispatcher.beginInvokeJSFromDotNet);
  connection.on('JS.RenderBatch', (browserRendererId: number, renderId: number, batchData: Uint8Array) => {
    try {
      console.log(`Render batch ${renderId} for renderer ${browserRendererId}.`);
      RenderTracker.trackRender(browserRendererId, renderId, batchData);
      for (let [nextId, nextData] = RenderTracker.getNextBatchToRender(browserRendererId);
        !!nextId && nextData !== undefined;
        [nextId, nextData] = RenderTracker.getNextBatchToRender(browserRendererId)) {
        renderBatch(browserRendererId, new OutOfProcessRenderBatch(nextData));
        completeBatch(browserRendererId, nextId);
      }
    } catch (ex) {
      // If there's a rendering exception, notify server *and* throw on client
      connection.send('OnRenderCompleted', renderId, ex.toString());
      throw ex;
    }
  });

  connection.onclose(error => circuitHandlers.forEach(h => h.onConnectionDown && h.onConnectionDown(error)));
  connection.on('JS.Error', error => unhandledError(connection, error));

  window['Blazor']._internal.forceCloseConnection = () => connection.stop();

  try {
    await connection.start();
  } catch (ex) {
    unhandledError(connection, ex);
  }

  DotNet.attachDispatcher({
    beginInvokeDotNetFromJS: (callId, assemblyName, methodIdentifier, dotNetObjectId, argsJson) => {
      connection.send('BeginInvokeDotNetFromJS', callId ? callId.toString() : null, assemblyName, methodIdentifier, dotNetObjectId || 0, argsJson);
    }
  });

  return connection;

  async function completeBatch(browserRendererId: number, renderId: number) {
    for (let i = 0; i < 3; i++) {
      try {
        await connection.send('OnRenderCompleted', renderId, null);
      }
      catch {
        console.log(`Failed to deliver completion notification for render '${renderId}' on attempt '${i}'.`);
      }
    }
  }
}

function unhandledError(connection: signalR.HubConnection, err: Error) {
  console.error(err);

  // Disconnect on errors.
  //
  // Trying to call methods on the connection after its been closed will throw.
  if (connection) {
    connection.stop();
  }
}

class RenderTracker {
  private static _trackedRenders = new Map<number, RendererRecord>();

  static getNextBatchToRender(browserRendererId: number): [number | undefined, Uint8Array | undefined] {
    const renderRecord = this._trackedRenders.get(browserRendererId)!;
    const { pendingRenders, nextRenderId } = renderRecord;
    const nextRenderBatch = pendingRenders.get(nextRenderId);
    if (nextRenderBatch !== undefined) {
      pendingRenders.delete(nextRenderId);
      renderRecord.nextRenderId++;
      console.log(`Rendering batch '${nextRenderId}'`);
      return [nextRenderId, nextRenderBatch];
    } else {
      return [undefined, undefined];
    }
  }

  public static trackRender(browserRendererId: number, renderId: number, data: Uint8Array) {
    const browserRendererMap = this._trackedRenders.get(browserRendererId) ||
      { nextRenderId: 2, pendingRenders: new Map<number, Uint8Array>() };

    this._trackedRenders.set(browserRendererId, browserRendererMap);
    if (renderId < browserRendererMap.nextRenderId) {
      // The server probably didn't get our ack in time and resent the batch.
      // TODO: Ack again that this render was completed. The server will also contain
      // logic to skip duplicate acks.
      return;
    }
    // This might be the next batch to render or another batch further in the future if we lost
    // a render on the way here.
    // We are assuming here that renders with the same renderId will always be equal.
    // We could check whether the render with the current id is already queued and check for
    // it to be identical, but I don't think it's worth it.
    browserRendererMap.pendingRenders.set(renderId, data);
  }
}

interface RendererRecord {
  nextRenderId: number;
  pendingRenders: Map<number, Uint8Array>;
}

boot();
