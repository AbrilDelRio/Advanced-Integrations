import * as React from "react";
import { PrimaryButton, DefaultButton, Icon } from "@fluentui/react";
import { TriggerRow } from "../../types";
import { EntityPicker } from "../pickers/EntityPicker";
import { FieldPicker } from "../pickers/FieldPicker";
import { Page, PageHeader, FieldGrid, Field, EmptyState } from "../shared/Layout";
type WorkingRow = TriggerRow & {
    _key: string;
};
const newTempKey = () => "new-" + Math.random().toString(36).slice(2, 10) + Date.now().toString(36);
const withKey = (r: TriggerRow): WorkingRow => ({ ...r, _key: r.mfd_advancedintegrationtriggerid ?? r._tempKey ?? "" });
const strip = (r: WorkingRow): TriggerRow => { const { _key, ...rest } = r; return rest; };
export const TriggersTab: React.FC<{
    rows: TriggerRow[];
    sourceEntity?: string | null;
    onChange: (rows: TriggerRow[]) => void;
}> = ({ rows, sourceEntity, onChange }) => {
    const working = React.useMemo(() => rows.map(withKey), [rows]);
    const [selectedKey, setSelectedKey] = React.useState<string | null>(working[0]?._key ?? null);
    const selected = working.find(r => r._key === selectedKey) ?? null;
    React.useEffect(() => {
        if (!sourceEntity)
            return;
        if (rows.some(r => r.mfd_tablename !== sourceEntity)) {
            onChange(rows.map(r => ({ ...r, mfd_tablename: sourceEntity })));
        }
    }, [sourceEntity]);
    const commit = (next: WorkingRow[]) => onChange(next.map(strip));
    const addRow = () => {
        const tempKey = newTempKey();
        const nr = withKey({ mfd_tablename: sourceEntity ?? null, _tempKey: tempKey });
        commit([...working, nr]);
        setSelectedKey(tempKey);
    };
    const updateSelected = (patch: Partial<TriggerRow>) => {
        commit(working.map(r => (r._key === selectedKey ? { ...r, ...patch } : r)));
    };
    const removeSelected = () => {
        if (!selectedKey)
            return;
        commit(working.filter(r => r._key !== selectedKey));
        setSelectedKey(null);
    };
    return (<Page wide>
      <PageHeader title="Triggers" subtitle="Columns on the source entity that fire this integration" actions={<PrimaryButton text="Add trigger" iconProps={{ iconName: "Add" }} onClick={addRow}/>}/>

      <div className="pccfg-split">
        <div className="pccfg-list">
          <div className="pccfg-list-head"><Icon iconName="LightningBolt"/> Triggers</div>
          <div className="pccfg-list-scroll">
            {working.map(r => (<div key={r._key} onClick={() => setSelectedKey(r._key)} className={r._key === selectedKey ? "pccfg-row is-selected" : "pccfg-row"}>
                <div className="pccfg-row-main">
                  <div className="pccfg-row-title">{r.mfd_columnname || "(unnamed column)"}</div>
                  <div className="pccfg-row-sub">{r.mfd_tablename || "-"}</div>
                </div>
              </div>))}
            {working.length === 0 && <EmptyState icon="LightningBolt" title="No triggers yet" detail="Add one to start."/>}
          </div>
        </div>

        <div className="pccfg-detail">
          {selected ? (<FieldGrid single>
              <Field>
                <EntityPicker label="Source table" required disabled value={sourceEntity} onChange={() => undefined}/>
              </Field>
              <Field>
                <FieldPicker label="Triggering column" required entity={sourceEntity} value={selected.mfd_columnname} onChange={(v) => updateSelected({ mfd_columnname: v ?? null })}/>
              </Field>
              <Field span>
                <DefaultButton text="Remove" iconProps={{ iconName: "Delete" }} onClick={removeSelected}/>
              </Field>
            </FieldGrid>) : (<EmptyState icon="LightningBolt" title="Nothing selected" detail="Select a trigger on the left, or add a new one."/>)}
        </div>
      </div>
    </Page>);
};
