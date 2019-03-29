import { attachRootComponentToElement } from '../../Rendering/Renderer'
import { internalFunctions as uriHelperFunctions } from '../../Services/UriHelper';

export default class CircuitRegistry {

  public static discoverPrerenderedCircuits(document: Document) {
    var circuits: CircuitEntry[] = [];
    var prerenderedCircuits = document
      .querySelectorAll('[data-component-id][data-circuit-id][data-renderer-id]');

    for (let i = 0; i < prerenderedCircuits.length; i++) {
      const element = prerenderedCircuits[i] as HTMLElement;
      const { componentId, circuitId, rendererId } = element.dataset;
      if (!circuits.some(c => c.circuitId == circuitId!)) {
        circuits.push(new CircuitEntry(componentId, circuitId, rendererId));
      }
    }

    return circuits;
  }

  public static async startCircuit(connection: signalR.HubConnection) {
    const result = await connection.invoke<string>(
      'StartCircuit',
      uriHelperFunctions.getLocationHref(),
      uriHelperFunctions.getBaseURI()
    );

    return result && new CircuitEntry(undefined, result, undefined);
  }
}

export class CircuitEntry {
  public connected = false;

  constructor(public componentId, public circuitId, public rendererId) {
  }

  public reconnect(reconnection: signalR.HubConnection) {
    return reconnection.invoke<boolean>('ConnectCircuit', this.circuitId);
  }

  public initialize() {
    const selector = `[data-component-id="${this.componentId}"][data-circuit-id="${this.circuitId}"][data-renderer-id="${this.rendererId}"]`;
    attachRootComponentToElement(Number.parseInt(this.rendererId!), selector, Number.parseInt(this.componentId!));
  }
}
