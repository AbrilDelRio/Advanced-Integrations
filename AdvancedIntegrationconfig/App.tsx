import * as React from "react";
import { Dropdown, IDropdownOption, PrimaryButton, DefaultButton, Spinner, SpinnerSize, MessageBar, MessageBarType, Icon, initializeIcons, } from "@fluentui/react";
import { AdvancedMappingDocument, AdvancedMappingSummary } from "./types";
import { listMappings, loadDocument, saveDocument, newDocument } from "./services/dataService";
import { GeneralSettingsTab } from "./components/tabs/GeneralSettingsTab";
import { TriggersTab } from "./components/tabs/TriggersTab";
import { SourceConditionsTab } from "./components/tabs/SourceConditionsTab";
import { DestinationConditionsTab } from "./components/tabs/DestinationConditionsTab";
import { MappingsTab } from "./components/tabs/MappingsTab";
import { EmptyState } from "./components/shared/Layout";
initializeIcons();
type SectionKey = "general" | "triggers" | "sourceConditions" | "destinationConditions" | "mappings";
const SECTIONS: {
    key: SectionKey;
    name: string;
    icon: string;
}[] = [
    { key: "general", name: "General", icon: "Settings" },
    { key: "triggers", name: "Triggers", icon: "LightningBolt" },
    { key: "sourceConditions", name: "Source Conditions", icon: "FilterSettings" },
    { key: "destinationConditions", name: "Destination Conditions", icon: "Filter" },
    { key: "mappings", name: "Mappings", icon: "MapLayers" },
];
export const App: React.FC<{
    initialConfigId: string | null;
}> = ({ initialConfigId }) => {
    const [summaries, setSummaries] = React.useState<AdvancedMappingSummary[]>([]);
    const [configId, setConfigId] = React.useState<string | null>(initialConfigId);
    const [doc, setDoc] = React.useState<AdvancedMappingDocument | null>(null);
    const [original, setOriginal] = React.useState<AdvancedMappingDocument | null>(null);
    const [section, setSection] = React.useState<SectionKey>("general");
    const [loading, setLoading] = React.useState(true);
    const [saving, setSaving] = React.useState(false);
    const [error, setError] = React.useState<string | null>(null);
    const [savedMsg, setSavedMsg] = React.useState<string | null>(null);
    const reloadList = React.useCallback(async () => {
        try {
            setSummaries(await listMappings());
        }
        catch (e) {
            setError("Could not load configurations: " + (e as Error).message);
        }
    }, []);
    React.useEffect(() => { void reloadList().finally(() => setLoading(false)); }, [reloadList]);
    React.useEffect(() => {
        if (!configId) {
            setOriginal(null);
            setLoading(false);
            return;
        }
        let cancelled = false;
        setLoading(true);
        loadDocument(configId)
            .then(d => {
            if (!cancelled) {
                setDoc(d);
                setOriginal(d);
            }
        })
            .catch(e => {
            if (!cancelled)
                setError("Could not load configuration: " + (e as Error).message);
        })
            .finally(() => {
            if (!cancelled)
                setLoading(false);
        });
        return () => { cancelled = true; };
    }, [configId]);
    const onNew = () => {
        setConfigId(null);
        setDoc(newDocument());
        setOriginal(null);
        setSection("general");
        setLoading(false);
        setError(null);
        setSavedMsg(null);
    };
    const onSave = async () => {
        if (!doc)
            return;
        if (!doc.header.mfd_eventforsourceentity || !doc.header.mfd_destinationentity) {
            setError("\"Source entity\" and \"Destination entity\" are required before saving.");
            return;
        }
        setSaving(true);
        setError(null);
        setSavedMsg(null);
        try {
            const result = await saveDocument(doc, original);
            setDoc(result.document);
            setOriginal(result.document);
            setConfigId(result.headerId);
            await reloadList();
            setSavedMsg("Configuration saved.");
        }
        catch (e) {
            setError("Save failed: " + (e as Error).message);
        }
        finally {
            setSaving(false);
        }
    };
    const options: IDropdownOption[] = summaries.map(s => ({ key: s.id, text: s.name + (s.enabled === false ? " (disabled)" : "") }));
    const isDirty = doc && JSON.stringify(doc) !== JSON.stringify(original);
    return (<div className="pccfg-root" style={{ display: "flex", flexDirection: "column", overflow: "hidden" }}>
      <div className="pccfg-appbar">
        <span className="pccfg-appbar-title">Advanced Integrations Configurator</span>
        <div className="pccfg-spacer"/>
        <Dropdown placeholder="Select a configuration" selectedKey={configId ?? undefined} options={options} styles={{ root: { minWidth: 280 } }} onChange={(_, o) => o && setConfigId(String(o.key))}/>
        <DefaultButton iconProps={{ iconName: "Add" }} text="New" onClick={onNew}/>
        <PrimaryButton iconProps={{ iconName: "Save" }} text={saving ? "Saving..." : "Save"} disabled={!doc || saving || !isDirty} onClick={onSave}/>
      </div>

      {error && <MessageBar messageBarType={MessageBarType.error} onDismiss={() => setError(null)}>{error}</MessageBar>}
      {savedMsg && <MessageBar messageBarType={MessageBarType.success} onDismiss={() => setSavedMsg(null)}>{savedMsg}</MessageBar>}

      <div className="pccfg-body">
        <nav className="pccfg-nav">
          {SECTIONS.map(s => (<button key={s.key} type="button" className={s.key === section ? "pccfg-nav-item is-selected" : "pccfg-nav-item"} onClick={() => setSection(s.key)}>
              <Icon iconName={s.icon}/>
              <span>{s.name}</span>
            </button>))}
        </nav>

        <main className="pccfg-content">
          {loading && <Spinner size={SpinnerSize.large} label="Loading..."/>}
          {!loading && !doc && (<EmptyState icon="Settings" title="No configuration selected" detail="Pick a configuration above or create a new one to begin."/>)}
          {!loading && doc && (<>
              {section === "general" && (<GeneralSettingsTab header={doc.header} onChange={(patch) => setDoc({ ...doc, header: { ...doc.header, ...patch } })}/>)}
              {section === "triggers" && (<TriggersTab rows={doc.triggers} sourceEntity={doc.header.mfd_eventforsourceentity} onChange={(triggers) => setDoc({ ...doc, triggers })}/>)}
              {section === "sourceConditions" && (<SourceConditionsTab rows={doc.sourceConditions} sourceEntity={doc.header.mfd_eventforsourceentity} onChange={(sourceConditions) => setDoc({ ...doc, sourceConditions })}/>)}
              {section === "destinationConditions" && (<DestinationConditionsTab rows={doc.destinationConditions} sourceEntity={doc.header.mfd_eventforsourceentity} destinationEntity={doc.header.mfd_destinationentity} onChange={(destinationConditions) => setDoc({ ...doc, destinationConditions })}/>)}
              {section === "mappings" && (<MappingsTab rows={doc.mappings} sourceEntity={doc.header.mfd_eventforsourceentity} destinationEntity={doc.header.mfd_destinationentity} onChange={(mappings) => setDoc({ ...doc, mappings })}/>)}
            </>)}
        </main>
      </div>
    </div>);
};
