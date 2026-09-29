import * as React from "react";
import * as ReactDOM from "react-dom";
import { IInputs, IOutputs } from "./generated/ManifestTypes";
import { App } from "./App";
import { registerMetadataHost } from "./services/metadataService";
import { registerDataHost } from "./services/dataService";

export class AdvancedIntegrationConfigurator implements ComponentFramework.StandardControl<IInputs, IOutputs> {
  private container: HTMLDivElement;

  public init(
    context: ComponentFramework.Context<IInputs>,
    notifyOutputChanged: () => void,
    state: ComponentFramework.Dictionary,
    container: HTMLDivElement,
  ): void {
    registerMetadataHost(context);
    registerDataHost(context);
    this.container = container;
    context.mode.trackContainerResize(true);
    this.renderControl(context);
  }

  public updateView(context: ComponentFramework.Context<IInputs>): void {
    registerMetadataHost(context);
    registerDataHost(context);
    this.renderControl(context);
  }

  public getOutputs(): IOutputs {
    return {};
  }

  public destroy(): void {
    ReactDOM.unmountComponentAtNode(this.container);
  }

  private renderControl(context: ComponentFramework.Context<IInputs>): void {
    let recordId: string | null = context.parameters.recordId?.raw ?? null;
    if (recordId && recordId.trim() === "") recordId = null;

    ReactDOM.render(React.createElement(App, { initialConfigId: recordId }), this.container);
  }
}
