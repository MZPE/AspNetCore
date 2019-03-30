import '@dotnet/jsinterop';
import './GlobalExports';
import * as signalR from '@aspnet/signalr';
import { MessagePackHubProtocol } from '@aspnet/signalr-protocol-msgpack';
import { fetchBootConfigAsync, loadEmbeddedResourcesAsync } from './BootCommon';
import { CircuitHandler } from './Platform/Circuits/CircuitHandler';
import { AutoReconnectCircuitHandler } from './Platform/Circuits/AutoReconnectCircuitHandler';
import CircuitRegistry from './Platform/Circuits/CircuitRegistry';
import RenderQueue, { BatchStatus } from './Platform/Circuits/RenderQueue';
import { ConsoleLogger, NullLogger } from './Platform/Logging/Loggers';
import { LogLevel, ILogger } from './Platform/Logging/ILogger';

async function boot() {

  // Replace creation with in development to see
  // traces in the browser.
  // In the future we will allow for users to configure this.
  const logger = NullLogger.instance;

  logger.log(LogLevel.Information, 'Booting blazor.');

  const circuitHandlers: CircuitHandler[] = [new AutoReconnectCircuitHandler()];
  window['Blazor'].circuitHandlers = circuitHandlers;

  // In the background, start loading the boot config and any embedded resources
  const embeddedResourcesPromise = fetchBootConfigAsync().then(bootConfig => {
    return loadEmbeddedResourcesAsync(bootConfig);
  });

  const initialConnection = await initializeConnection(circuitHandlers, logger);

  const circuits = CircuitRegistry.discoverPrerenderedCircuits(document);
  for (let i = 0; i < circuits.length; i++) {
    const circuit = circuits[i];
    circuit.initialize();
  }

  // Ensure any embedded resources have been loaded before starting the app
  await embeddedResourcesPromise;

  const startCircuit = await CircuitRegistry.startCircuit(initialConnection);

  if (!startCircuit) {
    console.log(`No preregistered components to render.`);
  }

  const reconnect = async () => {
    const reconnection = await initializeConnection(circuitHandlers, logger);
    var results = await Promise.all(circuits.map(circuit => circuit.reconnect(reconnection)));

    if (reconnectionFailed(results)) {
      return false;
    }

    circuitHandlers.forEach(h => h.onConnectionUp && h.onConnectionUp());
    return true;
  };

  window['Blazor'].reconnect = reconnect;

  const reconnectTask = reconnect();

  if (!!startCircuit) {
    circuits.push(startCircuit);
  }

  await reconnectTask;

  function reconnectionFailed(results: boolean[]) {
    return !results.reduce((current, next) => current && next, true);
  }
}

async function initializeConnection(circuitHandlers: CircuitHandler[], logger: ILogger): Promise<signalR.HubConnection> {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('_blazor')
    .withHubProtocol(new MessagePackHubProtocol())
    .configureLogging(signalR.LogLevel.Information)
    .build();

  connection.on('JS.BeginInvokeJS', DotNet.jsCallDispatcher.beginInvokeJSFromDotNet);
  connection.on('JS.RenderBatch', (browserRendererId: number, renderId: number, batchData: Uint8Array) => {
    logger.log(LogLevel.Information, `Received render batch for ${browserRendererId} with id ${renderId} and ${batchData.byteLength} bytes.`)

    const queue = RenderQueue.getOrCreateQueue(browserRendererId, logger);

    const result = queue.enqueue(renderId, batchData);
    if (result === BatchStatus.Processed) {
      connection.send('OnRenderCompleted', renderId);
    }

    queue.renderPendingBatches(connection);
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

boot();
