

export class PrerenderedCircuit {
  public static finPrerenderedCircuits() {
    const prerendered = document.querySelectorAll("[data-circuit-id]") as NodeListOf<HTMLElement>;

    let result: { circuitId: string | undefined, componentId: string | undefined }[] = [];

    for (let i = 0; i < prerendered.length; i++) {
      const element = prerendered[i];
      const data = {
        circuitId: element.dataset.circuitId,
        componentId: element.dataset.componentId
      };

      result.push(data);
    }
  }
}
