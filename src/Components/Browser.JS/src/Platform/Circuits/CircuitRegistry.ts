import { attachRootComponentToElement } from '../../Rendering/Renderer'
import { internalFunctions as uriHelperFunctions } from '../../Services/UriHelper';

export default class CircuitRegistry {

  public static discoverPrerenderedCircuits(document: Document) {

    var commentPairs = resolveCommentPairs(document);
    var circuits: ComponentEntry[] = [];

    for (let i = 0; i < commentPairs.length; i++) {
      const pair = commentPairs[i];
      if (!pair.valid) {
        reportInvalidPair(pair);
      } else {
        const entry = new ComponentEntry(pair.start.componentId!, pair.start.circuitId!, pair.start.rendererId!);
        entry.placeholder = pair;
        circuits.push(entry);
      }
    }

    // var prerenderedCircuits = document
    //   .querySelectorAll('[data-component-id][data-circuit-id][data-renderer-id]');

    // for (let i = 0; i < prerenderedCircuits.length; i++) {
    //   const element = prerenderedCircuits[i] as HTMLElement;
    //   const { componentId, circuitId, rendererId } = element.dataset;
    //   if (!circuits.some(c => c.circuitId == circuitId!)) {
    //     circuits.push(new ComponentEntry(componentId, circuitId, rendererId));
    //   }
    // }

    return circuits;
  }

  public static async startCircuit(connection: signalR.HubConnection) {
    const result = await connection.invoke<string>(
      'StartCircuit',
      uriHelperFunctions.getLocationHref(),
      uriHelperFunctions.getBaseURI()
    );

    return result && new ComponentEntry(undefined, result, undefined);
  }
}

function reportInvalidPair(pair: ComponentResult) {
}

export class ComponentEntry {
  public placeholder?: ComponentResult = undefined;

  constructor(public componentId, public circuitId, public rendererId) {
  }

  public reconnect(reconnection: signalR.HubConnection) {
    return reconnection.invoke<boolean>('ConnectCircuit', this.circuitId);
  }

  public initialize() {
    if (!this.placeholder) {
      const selector = `[data-component-id="${this.componentId}"][data-circuit-id="${this.circuitId}"][data-renderer-id="${this.rendererId}"]`;
      attachRootComponentToElement(Number.parseInt(this.rendererId!), selector, Number.parseInt(this.componentId!));
    } else {
      let { start, end } = this.placeholder;
      attachRootComponentToElement(this.rendererId!, { start: start.node!, end: end!.node! }, this.componentId!);
    }
  }
}

function resolveCommentPairs(node: Node): ComponentResult[] {
  if (!node.hasChildNodes()) {
    return [];
  }

  let result: ComponentResult[] = [];
  const children = node.childNodes;
  let i = 0;
  let childrenLength = children.length;
  while (i < childrenLength) {
    let currentChildNode = children[i];
    let startComponent = getComponentStartComment(currentChildNode)
    if (!startComponent) {
      i++;
      let childResults = resolveCommentPairs(currentChildNode);
      for (let j = 0; j < childResults.length; j++) {
        const childResult = childResults[j];
        result.push(childResult);
      }
      continue;
    }

    if (startComponent.isWellFormed) {
      let endComponent = getComponentEndComment(startComponent, children, i + 1, childrenLength);
      result.push({ valid: startComponent.isWellFormed && endComponent.isWellFormed, start: startComponent, end: endComponent });
      i = endComponent.index + 1;
    } else {
      result.push({ valid: false, start: startComponent, end: undefined });
      i = i + 1;
    }
  }

  return result;
}

interface ComponentResult {
  valid: boolean;
  start: StartComponentComment;
  end?: EndComponentComment;
}

function getComponentStartComment(node: Node): StartComponentComment | undefined {
  if (node.nodeType !== Node.COMMENT_NODE) {
    return;
  }

  if (node.textContent) {
    const componentStartComment = /\W+M.A.C.Component:[^{]*(?<json>.*)$/;

    let definition = componentStartComment.exec(node.textContent);
    let json = definition && definition['groups'] && definition['groups'].json;
    if (json) {
      try {
        let { componentId, circuitId, rendererId } = JSON.parse(json);
        return {
          isWellFormed: !!componentId && !!circuitId && !!rendererId,
          kind: ComponentCommentKind.Start,
          node,
          circuitId,
          rendererId: Number.parseInt(rendererId),
          componentId: Number.parseInt(componentId),
        };
      } catch (error) {
        return { isWellFormed: false, kind: ComponentCommentKind.Start };
      }
    }
  }
}

function getComponentEndComment(component: StartComponentComment, children: NodeList, index: number, end: number): EndComponentComment {
  var malformed: EndComponentComment[] = [];
  for (let i = index; i < end; i++) {
    const node = children[i];
    if (node.nodeType !== Node.COMMENT_NODE) {
      continue;
    }

    if (!node.textContent) {
      continue;
    }

    const componentEndComment = /\W+M.A.C.Component:\W+(?<componentId>\d+)\W+$/;

    let definition = componentEndComment.exec(node.textContent!);
    let rawComponentId = definition && definition['groups'] && definition['groups'].componentId;
    if (!rawComponentId) {
      continue;
    }

    try {
      let componentId = Number.parseInt(rawComponentId);
      if (componentId === component.componentId) {
        return { isWellFormed: true, kind: ComponentCommentKind.End, componentId, node, index: i, malformed };
      } else {
        malformed.push({ isWellFormed: false, kind: ComponentCommentKind.End, node, index: i });
      }
    } catch (error) {
      malformed.push({ isWellFormed: false, kind: ComponentCommentKind.End, node, index: i, error });
    }
  }

  return { isWellFormed: false, kind: ComponentCommentKind.End, malformed, index: end };
}

interface EndComponentComment {
  isWellFormed: boolean;
  kind: ComponentCommentKind.End;
  malformed?: EndComponentComment[];
  componentId?: number;
  node?: Node;
  error?: Error;
  index: number;
}

interface StartComponentComment {
  isWellFormed: boolean;
  kind: ComponentCommentKind.Start;
  node?: Node,
  rendererId?: number;
  componentId?: number;
  circuitId?: string;
}

enum ComponentCommentKind {
  Start,
  End
}
