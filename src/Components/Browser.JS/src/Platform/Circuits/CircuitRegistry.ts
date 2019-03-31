import { attachRootComponentToElement } from '../../Rendering/Renderer';
import { internalFunctions as uriHelperFunctions } from '../../Services/UriHelper';
import { ILogger, LogLevel } from '../Logging/ILogger';

interface InvalidEndComponentComment {
  isWellFormed: false;
  kind: ComponentCommentKind.End;
  malformed?: InvalidEndComponentComment[];
  componentId?: number;
  node?: Node;
  error?: Error;
  index: number;
}

interface ValidEndComponentComment {
  isWellFormed: true;
  kind: ComponentCommentKind.End;
  malformed?: InvalidEndComponentComment[];
  componentId: number;
  node: Node;
  index: number;
}

type EndComponentComment = ValidEndComponentComment | InvalidEndComponentComment;

interface InvalidStartComponentComment {
  isWellFormed: false;
  kind: ComponentCommentKind.Start;
  node?: Node;
  rendererId?: number;
  componentId?: number;
  circuitId?: string;
}

interface ValidStartComponentComment {
  isWellFormed: true;
  kind: ComponentCommentKind.Start;
  node: Node;
  rendererId: number;
  componentId: number;
  circuitId: string;
}

type StartComponentComment = ValidStartComponentComment | InvalidStartComponentComment;

enum ComponentCommentKind {
  Start,
  End
}

interface InvalidComponentResult {
  valid: false;
  start?: StartComponentComment;
  end?: EndComponentComment;
}

interface ValidComponentResult {
  valid: true;
  start: ValidStartComponentComment;
  end: ValidEndComponentComment;
}

type ComponentResult = ValidComponentResult | InvalidComponentResult;

export class ComponentEntry {
  public placeholder: ComponentResult;
  public componentId: number;
  public circuitId: string;
  public rendererId: number;

  public constructor(componentId: number, circuitId: string, rendererId: number, placeholder: ComponentResult) {
    this.componentId = componentId;
    this.circuitId = circuitId;
    this.rendererId = rendererId;
    this.placeholder = placeholder;
  }

  public reconnect(reconnection: signalR.HubConnection): Promise<boolean> {
    return reconnection.invoke<boolean>('ConnectCircuit', this.circuitId);
  }

  public initialize(): void {
    if (!this.placeholder.valid) {
      throw new Error("Can't initialize an invalid component.");
    } else {
      const startEndPair = { start: this.placeholder.start.node, end: this.placeholder.end.node };
      attachRootComponentToElement(this.rendererId, startEndPair, this.componentId);
    }
  }
}

export default class CircuitRegistry {

  public static discoverPrerenderedCircuits(document: Document, logger: ILogger): ComponentEntry [] {
    const commentPairs = CircuitRegistry.resolveCommentPairs(document);
    const circuits: ComponentEntry[] = [];

    for (let i = 0; i < commentPairs.length; i++) {
      const pair = commentPairs[i];
      if (!pair.valid) {
        CircuitRegistry.reportInvalidPair(pair, logger);
      } else {
        const entry = new ComponentEntry(pair.start.componentId, pair.start.circuitId, pair.start.rendererId, pair);
        entry.placeholder = pair;
        circuits.push(entry);
      }
    }

    return circuits;
  }

  public static async startCircuit(connection: signalR.HubConnection): Promise<ComponentEntry | undefined> {
    const result = await connection.invoke<string>(
      'StartCircuit',
      uriHelperFunctions.getLocationHref(),
      uriHelperFunctions.getBaseURI()
    );

    if(result){
      return new ComponentEntry(-1, result, -1, {valid: false });
    }else{
      return undefined;
    }
  }

  private static reportInvalidPair(pair: ComponentResult, logger: ILogger): void {
    const nodeStartText = pair.start && pair.start.node && pair.start.node.textContent;
    const nodeEndText = pair.end && pair.end.node && pair.end.node.textContent;
    logger.log(LogLevel.Warning, `Invalid component definition found ${nodeStartText} - ${nodeEndText}`);
  }

  private static resolveCommentPairs(node: Node): ComponentResult[] {
    if (!node.hasChildNodes()) {
      return [];
    }

    const result: ComponentResult[] = [];
    const children = node.childNodes;
    let i = 0;
    const childrenLength = children.length;
    while (i < childrenLength) {
      const currentChildNode = children[i];
      const startComponent = CircuitRegistry.getComponentStartComment(currentChildNode);
      if (!startComponent) {
        i++;
        const childResults = CircuitRegistry.resolveCommentPairs(currentChildNode);
        for (let j = 0; j < childResults.length; j++) {
          const childResult = childResults[j];
          result.push(childResult);
        }
        continue;
      }

      if (startComponent.isWellFormed) {
        const endComponent = CircuitRegistry.getComponentEndComment(startComponent, children, i + 1, childrenLength);
        if(startComponent.isWellFormed && endComponent.isWellFormed){
          result.push({ valid: startComponent.isWellFormed && endComponent.isWellFormed, start: startComponent, end: endComponent });
        }else{
          result.push({ valid: false, start: startComponent, end: endComponent });
        }
        i = endComponent.index + 1;
      } else {
        result.push({ valid: false, start: startComponent, end: undefined });
        i = i + 1;
      }
    }

    return result;
  }

  private static getComponentStartComment(node: Node): StartComponentComment | undefined {
    if (node.nodeType !== Node.COMMENT_NODE) {
      return;
    }

    if (node.textContent) {
      const componentStartComment = /\W+M.A.C.Component:[^{]*(?<json>.*)$/;

      const definition = componentStartComment.exec(node.textContent);
      const json = definition && definition['groups'] && definition['groups'].json;
      if (json) {
        try {
          const { componentId, circuitId, rendererId } = JSON.parse(json);
          const allComponents = !!componentId && !!circuitId && !!rendererId;
          if(allComponents){
            return {
              isWellFormed: true,
              kind: ComponentCommentKind.Start,
              node,
              circuitId,
              rendererId: Number.parseInt(rendererId),
              componentId: Number.parseInt(componentId),
            };
          }else{
            return {
              isWellFormed: false,
              kind: ComponentCommentKind.Start,
              node,
              circuitId,
              rendererId: -1,
              componentId: -1,
            };
          }
        } catch (error) {
          return { isWellFormed: false, kind: ComponentCommentKind.Start };
        }
      }
    }
  }

  private static getComponentEndComment(component: StartComponentComment, children: NodeList, index: number, end: number): EndComponentComment {
    const malformed: InvalidEndComponentComment[] = [];
    for (let i = index; i < end; i++) {
      const node = children[i];
      if (node.nodeType !== Node.COMMENT_NODE) {
        continue;
      }

      if (!node.textContent) {
        continue;
      }

      const componentEndComment = /\W+M.A.C.Component:\W+(?<componentId>\d+)\W+$/;

      const definition = componentEndComment.exec(node.textContent);
      const rawComponentId = definition && definition['groups'] && definition['groups'].componentId;
      if (!rawComponentId) {
        continue;
      }

      try {
        const componentId = Number.parseInt(rawComponentId);
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
}
