import * as React from "react";
import { PrimaryButton, DefaultButton, TextField, Icon, Toggle, TooltipHost, MessageBar, MessageBarType } from "@fluentui/react";
import { DndContext, closestCenter, PointerSensor, useSensor, useSensors, DragEndEvent, } from "@dnd-kit/core";
import { SortableContext, verticalListSortingStrategy, useSortable, arrayMove, } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { MappingRow } from "../../types";
import { EntityPicker } from "../pickers/EntityPicker";
import { FieldPicker } from "../pickers/FieldPicker";
import { DefaultValueEditor } from "../pickers/DefaultValueEditor";
import { TypeCompatBadge } from "../shared/TypeCompatBadge";
import { AttrMeta, getLookupTarget } from "../../services/metadataService";
import { Page, PageHeader, FieldGrid, Field, EmptyState } from "../shared/Layout";
type WorkingRow = MappingRow & {
    _key: string;
};
const newTempKey = () => "new-" + Math.random().toString(36).slice(2, 10) + Date.now().toString(36);
const withKey = (r: MappingRow): WorkingRow => ({ ...r, _key: r.mfd_advancedintegrationmappingid ?? r._tempKey ?? "" });
const strip = (r: WorkingRow): MappingRow => { const { _key, ...rest } = r; return rest; };
const SortableRow: React.FC<{
    row: WorkingRow;
    selected: boolean;
    onClick: () => void;
}> = ({ row, selected, onClick }) => {
    const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: row._key });
    const style: React.CSSProperties = {
        transform: CSS.Transform.toString(transform), transition, opacity: isDragging ? 0.5 : 1,
    };
    return (<div ref={setNodeRef} style={style} onClick={onClick} className={selected ? "pccfg-row is-selected" : "pccfg-row"}>
      <span {...attributes} {...listeners} onClick={(e) => e.stopPropagation()} className="pccfg-grip">
        <Icon iconName="GripperDotsVertical"/>
      </span>
      <div className="pccfg-row-main">
        <div className="pccfg-row-title">{row.mfd_code || "(new mapping)"}</div>
        <div className="pccfg-row-sub">
          {row.mfd_usedefaultvalue
            ? "(default)"
            : row.mfd_userelatedentity
                ? `${row.mfd_lookupfield ?? "?"} → ${row.mfd_relatedentityfield ?? "?"}`
                : row.mfd_sourcecolumn ?? "?"}{" -> "}{row.mfd_destinationcolumn ?? "?"}
        </div>
      </div>
      {row.mfd_usecondition && <TooltipHost content="Uses sub-conditions"><Icon iconName="FilterSettings" styles={{ root: { color: "#605e5c" } }}/></TooltipHost>}
    </div>);
};
const RelatedEntityFieldPicker: React.FC<{
    entity: string | null;
    value?: string | null;
    onChange: (logicalName: string | undefined, meta?: AttrMeta) => void;
    required?: boolean;
}> = ({ entity, value, onChange, required }) => (
    <FieldPicker
        label="Related Entity Field"
        required={required}
        entity={entity}
        value={value}
        onChange={onChange}
        placeholder={entity ? "Pick a column from the related entity…" : "Select a Lookup Field first…"}
        disabled={!entity}
    />
);

export const MappingsTab: React.FC<{
    rows: MappingRow[];
    sourceEntity?: string | null;
    destinationEntity?: string | null;
    onChange: (rows: MappingRow[]) => void;
}> = ({ rows, sourceEntity, destinationEntity, onChange }) => {
    const working = React.useMemo(() => rows.map(withKey).sort((a, b) => (a.mfd_ordercolumn ?? 0) - (b.mfd_ordercolumn ?? 0)), [rows]);
    const [selectedKey, setSelectedKey] = React.useState<string | null>(working[0]?._key ?? null);
    const selected = working.find(r => r._key === selectedKey) ?? null;
    const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 5 } }));
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
    const [relatedEntity, setRelatedEntity] = React.useState<string | null>(null);
    const isLookupDest = destMeta?.attributeType === "Lookup" || destMeta?.attributeType === "Customer" || destMeta?.attributeType === "Owner";

    React.useEffect(() => {
        let cancelled = false;

        const loadRelatedEntity = async () => {
            if (!selected?.mfd_userelatedentity || !sourceEntity || !selected.mfd_lookupfield) {
                setRelatedEntity(null);
                return;
            }

            const target = await getLookupTarget(sourceEntity, selected.mfd_lookupfield);
            if (!cancelled) {
                setRelatedEntity(target);
            }
        };

        void loadRelatedEntity();

        return () => {
            cancelled = true;
        };
    }, [selected?.mfd_userelatedentity, selected?.mfd_lookupfield, selected?.mfd_relatedentityfield, sourceEntity]);
    React.useEffect(() => {
        if (!selected) {
            setSrcMeta(undefined);
            setDestMeta(undefined);
            setRelatedEntity(null);
            return;
        }

        if (selected.mfd_userelatedentity) {
            setSrcMeta(undefined);
        }
    }, [selectedKey, selected?.mfd_userelatedentity]);

    const commit = (next: WorkingRow[]) => onChange(next.map(strip));
    const addRow = () => {
        const maxOrder = Math.max(0, ...working.map(r => r.mfd_ordercolumn ?? 0));
        const tempKey = newTempKey();
        const nr = withKey({
            mfd_sourceentity: sourceEntity ?? null, mfd_destinationentity: destinationEntity ?? null,
            mfd_ordercolumn: maxOrder + 1, _tempKey: tempKey,
        });
        commit([...working, nr]);
        setSelectedKey(tempKey);
    };
    const updateSelected = (patch: Partial<MappingRow>) => {
        commit(working.map(r => (r._key === selectedKey ? { ...r, ...patch } : r)));
    };
    const removeSelected = () => {
        if (!selectedKey)
            return;
        commit(working.filter(r => r._key !== selectedKey));
        setSelectedKey(null);
    };
    const onDragEnd = (e: DragEndEvent) => {
        const { active, over } = e;
        if (!over || active.id === over.id)
            return;
        const oldIdx = working.findIndex(r => r._key === active.id);
        const newIdx = working.findIndex(r => r._key === over.id);
        commit(arrayMove(working, oldIdx, newIdx).map((r, i) => ({ ...r, mfd_ordercolumn: i + 1 })));
    };
    return (<Page wide>
      <PageHeader title="Mappings" subtitle={`${working.length} mapping(s) - drag to reorder`} actions={<PrimaryButton text="Add mapping" iconProps={{ iconName: "Add" }} onClick={addRow}/>}/>

      <div className="pccfg-split">
        <div className="pccfg-list">
          <div className="pccfg-list-head"><Icon iconName="MapLayers"/> Mappings</div>
          <div className="pccfg-list-scroll">
            <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
              <SortableContext items={working.map(r => r._key)} strategy={verticalListSortingStrategy}>
                {working.map(r => <SortableRow key={r._key} row={r} selected={r._key === selectedKey} onClick={() => setSelectedKey(r._key)}/>)}
              </SortableContext>
            </DndContext>
            {working.length === 0 && <EmptyState icon="MapLayers" title="No mappings yet" detail="Add one to start."/>}
          </div>
        </div>

        <div className="pccfg-detail">
          {selected ? (<FieldGrid single>
              {selected.mfd_code && (<Field><TextField label="Code" value={selected.mfd_code} disabled description="Auto-generated by Dataverse"/></Field>)}
              <Field><TextField label="Description" multiline rows={2} value={selected.mfd_description ?? ""} onChange={(_, v) => updateSelected({ mfd_description: v ?? "" })}/></Field>

              <Field>
                <Toggle label="Use default value (no source column)" checked={!!selected.mfd_usedefaultvalue} onChange={(_, c) => updateSelected({ mfd_usedefaultvalue: !!c, mfd_sourcecolumn: c ? null : selected.mfd_sourcecolumn })}/>
              </Field>

              {!selected.mfd_usedefaultvalue && (<>
                  <Field>
                    <Toggle
                      label="Use related entity as source"
                      checked={!!selected.mfd_userelatedentity}
                      onChange={(_, checked) => {
                        const useRelatedEntity = !!checked;
                        updateSelected({
                            mfd_userelatedentity: useRelatedEntity,
                            mfd_sourcecolumn: useRelatedEntity ? null : selected.mfd_sourcecolumn,
                            mfd_lookupfield: useRelatedEntity ? selected.mfd_lookupfield : null,
                            mfd_relatedentityfield: useRelatedEntity ? selected.mfd_relatedentityfield : null,
                        });
                        if (!useRelatedEntity) {
                            setSrcMeta(undefined);
                            setRelatedEntity(null);
                        }
                      }}
                    />
                  </Field>
                  <Field><EntityPicker label="Source entity" required disabled value={sourceEntity} onChange={() => undefined}/></Field>

                  {!selected.mfd_userelatedentity && (
                    <Field>
                      <FieldPicker
                        label="Source column"
                        required
                        entity={sourceEntity}
                        value={selected.mfd_sourcecolumn}
                        onChange={(v, meta) => {
                            updateSelected({ mfd_sourcecolumn: v ?? null });
                            setSrcMeta(meta);
                        }}
                      />
                    </Field>
                  )}

                  {selected.mfd_userelatedentity && (
                    <>
                      <Field>
                        <FieldPicker
                          label="Lookup Field"
                          required
                          entity={sourceEntity}
                          typeFilter={["Lookup", "Customer", "Owner"]}
                          value={selected.mfd_lookupfield}
                          onChange={(v) => {
                            updateSelected({
                                mfd_lookupfield: v ?? null,
                                mfd_relatedentityfield: null,
                            });
                            setRelatedEntity(null);
                          }}
                        />
                      </Field>
                      <Field>
                        <RelatedEntityFieldPicker
                          entity={relatedEntity}
                          value={selected.mfd_relatedentityfield}
                          onChange={(v) => updateSelected({ mfd_relatedentityfield: v ?? null })}
                          required
                        />
                      </Field>
                    </>
                  )}
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

              {isLookupDest && (<>
                  <Field><FieldPicker label="Lookup field" entity={destinationEntity} typeFilter={["Lookup", "Customer", "Owner"]} value={selected.mfd_lookupfield} onChange={(v) => updateSelected({ mfd_lookupfield: v ?? null })}/></Field>
                  <Field><FieldPicker label="Display name column (shown in dashboard grid)" entity={destinationEntity} value={selected.mfd_displaynamecolumn} onChange={(v) => updateSelected({ mfd_displaynamecolumn: v ?? null })} placeholder="Column used to render the friendly name"/></Field>
                </>)}

              <Field><DefaultValueEditor label={selected.mfd_usedefaultvalue ? "Default value" : "Default value if none found"} destEntity={destinationEntity} destMeta={destMeta} value={selected.mfd_defaultvalueifnonefound} onChange={(v) => updateSelected({ mfd_defaultvalueifnonefound: v })}/></Field>

              <Field>
                <Toggle label="Use sub-conditions for this mapping" checked={!!selected.mfd_usecondition} onChange={(_, c) => updateSelected({ mfd_usecondition: !!c })}/>
              </Field>
              {selected.mfd_usecondition && (<Field span>
                  <MessageBar messageBarType={MessageBarType.info}>
                    Sub-condition rows for a specific mapping aren't configurable here yet - the schema doesn't
                    have a table linking conditions to an individual mapping. Let us know where these should be
                    stored (a new table, or a lookup added to an existing condition table) and this can be wired up.
                  </MessageBar>
                </Field>)}

              <Field span>
                <DefaultButton text="Remove" iconProps={{ iconName: "Delete" }} onClick={removeSelected}/>
              </Field>
            </FieldGrid>) : (<EmptyState icon="MapLayers" title="Nothing selected" detail="Select a mapping on the left, or add a new one."/>)}
        </div>
      </div>
    </Page>);
};
