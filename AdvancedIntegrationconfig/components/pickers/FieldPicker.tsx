import * as React from "react";
import { ComboBox, IComboBoxOption, IComboBox, Spinner, SpinnerSize } from "@fluentui/react";
import { getAttributes, AttrMeta } from "../../services/metadataService";

const TYPE_ICON: Record<string, string> = {
  String: "TextField", Memo: "AlignLeft", Integer: "NumberField", BigInt: "NumberField",
  Decimal: "NumberField", Double: "NumberField", Money: "Money", DateTime: "Calendar",
  Boolean: "ToggleRight", Lookup: "Link", Customer: "Link", Owner: "Contact",
  Picklist: "DropdownList", MultiSelectPicklist: "MultiSelect", State: "StatusCircleInner",
  Status: "StatusCircleInner", Uniqueidentifier: "Fingerprint",
};

export const FieldPicker: React.FC<{
  entity?: string | null;
  value?: string | null;
  onChange: (logicalName: string | undefined, meta?: AttrMeta) => void;
  label?: string;
  placeholder?: string;
  typeFilter?: string[];
  writableOnly?: boolean;
  disabled?: boolean;
  required?: boolean;
}> = ({ entity, value, onChange, label, placeholder, typeFilter, writableOnly, disabled, required }) => {
  const [attrs, setAttrs] = React.useState<AttrMeta[] | null>(null);
  const [filterText, setFilterText] = React.useState<string>("");

  React.useEffect(() => {
    let cancelled = false;
    setAttrs(null);
    setFilterText("");
    if (!entity) { setAttrs([]); return; }
    getAttributes(entity).then(a => { if (!cancelled) setAttrs(a); }).catch(() => { if (!cancelled) setAttrs([]); });
    return () => { cancelled = true; };
  }, [entity]);

  const metaByName = React.useMemo(() => new Map((attrs ?? []).map(a => [a.logicalName, a])), [attrs]);

  const baseOptions: IComboBoxOption[] = React.useMemo(() => {
    let list = attrs ?? [];
    if (writableOnly) list = list.filter(a => a.isValidForUpdate || a.isValidForCreate);
    if (typeFilter?.length) list = list.filter(a => typeFilter.includes(a.attributeType));
    return list.map(a => ({
      key: a.logicalName,
      text: `${a.displayName}  ·  ${a.logicalName}`,
      data: a,
      styles: { optionText: { whiteSpace: "normal" } },
    }));
  }, [attrs, typeFilter, writableOnly]);

  const filteredOptions = React.useMemo(() => {
    if (!filterText.trim()) return baseOptions;
    const lower = filterText.trim().toLowerCase();
    return baseOptions.filter(o => 
      o.text.toLowerCase().includes(lower) || 
      String(o.key).toLowerCase().includes(lower)
    );
  }, [baseOptions, filterText]);

  if (!entity) {
    return (
      <ComboBox label={label} placeholder="Pick the entity first…" disabled options={[]} />
    );
  }
  if (attrs === null) {
    return <Spinner size={SpinnerSize.small} label={label ? `${label}…` : "Loading fields…"} labelPosition="right" />;
  }

  const onSel = (_: React.FormEvent<IComboBox>, opt?: IComboBoxOption, _index?: number, valueTyped?: string) => {
    if (opt) {
      onChange(String(opt.key), metaByName.get(String(opt.key)));
      setFilterText("");
    } else if (valueTyped !== undefined && !valueTyped.trim()) {
      onChange(undefined);
      setFilterText("");
    }
  };

  const knownValue = value ? baseOptions.some(o => o.key === value) || metaByName.has(value) : true;
  
  const displayOptions = value && !baseOptions.some(o => o.key === value)
    ? [{ key: value, text: `${value}  ·  (not in list)` } as IComboBoxOption, ...filteredOptions]
    : filteredOptions;

  const selectedOpt = baseOptions.find(o => o.key === value);

  return (
    <ComboBox
      label={label}
      required={required}
      placeholder={placeholder ?? "Pick a column…"}
      allowFreeform={true}
      autoComplete="on"
      useComboBoxAsMenuWidth
      disabled={disabled}
      selectedKey={value ?? null}
      text={value && !selectedOpt ? `${value}  ·  (not in list)` : undefined}
      options={displayOptions}
      onPendingValueChanged={(_option?: IComboBoxOption, _index?: number, pending?: string) => {
        if (pending !== undefined) {
          setFilterText(pending);
        }
      }}
      onChange={onSel}
      errorMessage={value && !knownValue ? "Column not found in current metadata" : undefined}
      onRenderOption={(o) => {
        const m = o?.data as AttrMeta | undefined;
        return (
          <span style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <i className={`ms-Icon ms-Icon--${(m && TYPE_ICON[m.attributeType]) || "Column"}`} style={{ opacity: 0.6 }} />
            <span>{o?.text}</span>
          </span>
        );
      }}
    />
  );
};