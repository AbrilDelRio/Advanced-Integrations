import * as React from "react";
import { Dropdown, IDropdownOption, Spinner, SpinnerSize } from "@fluentui/react";
import { getOptionSet } from "../../services/metadataService";

/** Single-select choice, values resolved live from Dataverse metadata — never hardcoded. */
export const MetaChoicePicker: React.FC<{
  entity: string;
  attribute: string;
  value?: number | null;
  onChange: (value: number | null) => void;
  label?: string;
  disabled?: boolean;
  required?: boolean;
}> = ({ entity, attribute, value, onChange, label, disabled, required }) => {
  const [options, setOptions] = React.useState<IDropdownOption[] | null>(null);

  React.useEffect(() => {
    let cancelled = false;
    setOptions(null);
    getOptionSet(entity, attribute)
      .then(opts => { if (!cancelled) setOptions(opts.map(o => ({ key: o.value, text: o.label }))); })
      .catch(() => { if (!cancelled) setOptions([]); });
    return () => { cancelled = true; };
  }, [entity, attribute]);

  if (options === null) return <Spinner size={SpinnerSize.small} label={label ? `${label}…` : "Loading options…"} labelPosition="right" />;

  return (
    <Dropdown label={label} required={required} disabled={disabled}
      selectedKey={typeof value === "number" ? value : undefined}
      options={options}
      onChange={(_, o) => onChange(o ? Number(o.key) : null)} />
  );
};

/** Multi-select choice — stores/reads a comma-separated list of numeric values. */
export const MetaMultiChoicePicker: React.FC<{
  entity: string;
  attribute: string;
  value?: string | null;    // CSV, e.g. "1,2"
  onChange: (value: string | null) => void;
  label?: string;
  disabled?: boolean;
}> = ({ entity, attribute, value, onChange, label, disabled }) => {
  const [options, setOptions] = React.useState<IDropdownOption[] | null>(null);

  React.useEffect(() => {
    let cancelled = false;
    setOptions(null);
    getOptionSet(entity, attribute)
      .then(opts => { if (!cancelled) setOptions(opts.map(o => ({ key: o.value, text: o.label }))); })
      .catch(() => { if (!cancelled) setOptions([]); });
    return () => { cancelled = true; };
  }, [entity, attribute]);

  if (options === null) return <Spinner size={SpinnerSize.small} label={label ? `${label}…` : "Loading options…"} labelPosition="right" />;

  const selectedKeys = (value ?? "").split(",").map(s => s.trim()).filter(Boolean).map(Number);

  return (
    <Dropdown label={label} multiSelect disabled={disabled}
      selectedKeys={selectedKeys}
      options={options}
      onChange={(_, o) => {
        if (!o) return;
        const next = o.selected
          ? [...selectedKeys, Number(o.key)]
          : selectedKeys.filter(k => k !== Number(o.key));
        onChange(next.length ? next.join(",") : null);
      }} />
  );
};
