import * as React from "react";
import { PrimaryButton, DefaultButton, TextField, Icon, Toggle } from "@fluentui/react";
import { DestinationConditionRow } from "../../types";
import { EntityPicker } from "../pickers/EntityPicker";
import { FieldPicker } from "../pickers/FieldPicker";
import { MetaChoicePicker } from "../pickers/MetaChoicePicker";
import { DefaultValueEditor } from "../pickers/DefaultValueEditor";
import { TypeCompatBadge } from "../shared/TypeCompatBadge";
import { AttrMeta } from "../../services/metadataService";
import { useMetaOptions, labelFor, labelImpliesNoValue } from "../shared/useMetaOptions";
import { Page, PageHeader, FieldGrid, Field, EmptyState } from "../shared/Layout";
const ENTITY = "mfd_advancedintegrationdestinationcondition";
type WorkingRow = DestinationConditionRow & {
    _key: string;
};
const newTempKey = () => "new-" + Math.random().toString(36).slice(2, 10) + Date.now().toString(36);
const withKey = (r: DestinationConditionRow): WorkingRow => ({ ...r, _key: r.mfd_advancedintegrationdestinationconditionid ?? r._tempKey ?? "" });
const strip = (r: WorkingRow): DestinationConditionRow => { const { _key, ...rest } = r; return rest; };
export const DestinationConditionsTab: React.FC<{
    rows: DestinationConditionRow[];
    sourceEntity?: string | null;
    destinationEntity?: string | null;
    onChange: (rows: DestinationConditionRow[]) => void;
}> = ({ rows, sourceEntity, destinationEntity, onChange }) => {
    const working = React.useMemo(() => rows.map(withKey), [rows]);
    const [selectedKey, setSelectedKey] = React.useState<string | null>(working[0]?._key ?? null);
    const selected = working.find(r => r._key === selectedKey) ?? null;
    const commit = (next: WorkingRow[]) => onChange(next.map(strip));
    React.useEffect(() => {
        if (!sourceEntity && !destinationEntity)
            return;
        const needsSync = rows.some(r => (sourceEntity && r.mfd_sourceentity !== sourceEntity) ||
            (destinationEntity && r.mfd_destinationentity !== destinationEntity));
        if (needsSync) {
            onChange(rows.map(r => ({
                ...r,
                mfd_sourceentity: sourceEntity ?? r.mfd_sourceentity,
                mfd_destinationentity: destinationEntity ?? r.mfd_destinationentity,
            })));
        }
    }, [sourceEntity, destinationEntity]);
    const [srcMeta, setSrcMeta] = React.useState<AttrMeta | undefined>();
    const [destMeta, setDestMeta] = React.useState<AttrMeta | undefined>();
    const operatorOptions = useMetaOptions(ENTITY, "mfd_operator");
    const opLabel = labelFor(operatorOptions, selected?.mfd_operator);
    const noValue = labelImpliesNoValue(opLabel);
    const dataTypeOptions = useMetaOptions(ENTITY, "mfd_datatype");
    const addRow = () => {
        const tempKey = newTempKey();
        const nr = withKey({ mfd_sourceentity: sourceEntity ?? null, mfd_destinationentity: destinationEntity ?? null, _tempKey: tempKey });
        commit([...working, nr]);
        setSelectedKey(tempKey);
    };
    const updateSelected = (patch: Partial<DestinationConditionRow>) => {
        commit(working.map(r => (r._key === selectedKey ? { ...r, ...patch } : r)));
    };
    const removeSelected = () => {
        if (!selectedKey)
            return;
        commit(working.filter(r => r._key !== selectedKey));
        setSelectedKey(null);
    };
    React.useEffect(() => {
        if (!selected || selected.mfd_datatype != null || !destMeta?.attributeType || !dataTypeOptions?.length)
            return;
        const match = dataTypeOptions.find(o => o.label.toLowerCase() === destMeta.attributeType.toLowerCase());
        if (match)
            updateSelected({ mfd_datatype: match.value });
    }, [destMeta, dataTypeOptions]);
    return (<Page wide>
      <PageHeader title="Destination Conditions" subtitle="Gate the write on values already present in the destination record" actions={<PrimaryButton text="Add condition" iconProps={{ iconName: "Add" }} onClick={addRow}/>}/>

      <div className="pccfg-split">
        <div className="pccfg-list">
          <div className="pccfg-list-head"><Icon iconName="FilterSettings"/> Destination conditions</div>
          <div className="pccfg-list-scroll">
            {working.map(r => (<div key={r._key} onClick={() => setSelectedKey(r._key)} className={r._key === selectedKey ? "pccfg-row is-selected" : "pccfg-row"}>
                <div className="pccfg-row-main">
                  <div className="pccfg-row-title">{r.mfd_code || "(new condition)"}</div>
                  <div className="pccfg-row-sub">
                    {r.mfd_sourcecolumn ?? "(default)"}{" -> "}{r.mfd_destinationcolumn ?? "?"}
                  </div>
                </div>
              </div>))}
            {working.length === 0 && <EmptyState icon="FilterSettings" title="No conditions yet" detail="Add one to start."/>}
          </div>
        </div>

        <div className="pccfg-detail">
          {selected ? (<FieldGrid single>
              {selected.mfd_code && (<Field><TextField label="Code" value={selected.mfd_code} disabled description="Auto-generated by Dataverse"/></Field>)}

              <Field>
                <Toggle label="Use default value" checked={!!selected.mfd_usedefaultvalue} onChange={(_, c) => updateSelected({ mfd_usedefaultvalue: !!c, mfd_sourcecolumn: c ? null : selected.mfd_sourcecolumn })}/>
              </Field>

              {!selected.mfd_usedefaultvalue && (<>
                  <Field><EntityPicker label="Source entity" required disabled value={sourceEntity} onChange={() => undefined}/></Field>
                  <Field><FieldPicker label="Source column" required entity={sourceEntity} value={selected.mfd_sourcecolumn} onChange={(v, meta) => { updateSelected({ mfd_sourcecolumn: v ?? null }); setSrcMeta(meta); }}/></Field>
                </>)}

              <Field><EntityPicker label="Destination entity" required disabled value={destinationEntity} onChange={() => undefined}/></Field>
              <Field>
                <div className="pccfg-inline">
                  <div style={{ flex: 1, minWidth: 220 }}>
                    <FieldPicker label="Destination column" required writableOnly entity={destinationEntity} value={selected.mfd_destinationcolumn} onChange={(v, meta) => { updateSelected({ mfd_destinationcolumn: v ?? null }); setDestMeta(meta); }}/>
                  </div>
                  {!selected.mfd_usedefaultvalue && <TypeCompatBadge sourceType={srcMeta?.attributeType} destType={destMeta?.attributeType}/>}
                </div>
              </Field>

              <Field><MetaChoicePicker label="Data type" entity={ENTITY} attribute="mfd_datatype" value={selected.mfd_datatype} onChange={(v) => updateSelected({ mfd_datatype: v })}/></Field>

              <Field><MetaChoicePicker label="Operator" required entity={ENTITY} attribute="mfd_operator" value={selected.mfd_operator} onChange={(v) => updateSelected({ mfd_operator: v })}/></Field>

              {(selected.mfd_usedefaultvalue || !noValue) && (<Field><DefaultValueEditor label={selected.mfd_usedefaultvalue ? "Default value" : "Comparison value"} destEntity={destinationEntity} destMeta={destMeta} value={selected.mfd_defaultvalue} onChange={(v) => updateSelected({ mfd_defaultvalue: v })}/></Field>)}

              <Field span>
                <DefaultButton text="Remove" iconProps={{ iconName: "Delete" }} onClick={removeSelected}/>
              </Field>
            </FieldGrid>) : (<EmptyState icon="FilterSettings" title="Nothing selected" detail="Select a condition on the left, or add a new one."/>)}
        </div>
      </div>
    </Page>);
};
