import * as React from "react";
import { ComboBox, IComboBox, IComboBoxOption, Spinner, SpinnerSize } from "@fluentui/react";
import { searchRecords, getRecordName } from "../../services/metadataService";


export const RecordLookupPicker: React.FC<{
  entity: string;
  value?: string | null;                 // stored GUID
  onChange: (id: string | null, name?: string) => void;
  label?: string;
  disabled?: boolean;
}> = ({ entity, value, onChange, label, disabled }) => {
  const [options, setOptions] = React.useState<IComboBoxOption[]>([]);
  const [loading, setLoading] = React.useState(false);
  const [resolvedName, setResolvedName] = React.useState<string | null>(null);
  const debounceRef = React.useRef<number | undefined>(undefined);

  // Resolve the display name of a pre-existing value so the field isn't blank.
  React.useEffect(() => {
    let cancelled = false;
    setResolvedName(null);
    if (!value || !entity) return;
    getRecordName(entity, value).then(n => { if (!cancelled) setResolvedName(n); }).catch(() => undefined);
    return () => { cancelled = true; };
  }, [entity, value]);

  const runSearch = React.useCallback((text: string) => {
    if (!entity) return;
    setLoading(true);
    searchRecords(entity, text)
      .then(rs => setOptions(rs.map(r => ({ key: r.id, text: r.name }))))
      .catch(() => setOptions([]))
      .finally(() => setLoading(false));
  }, [entity]);

  React.useEffect(() => { runSearch(""); }, [runSearch]);

  const onInputChange = (text: string): string => {
    window.clearTimeout(debounceRef.current);
    debounceRef.current = window.setTimeout(() => runSearch(text), 300);
    return text;
  };

  const onSel = (_: React.FormEvent<IComboBox>, opt?: IComboBoxOption) => {
    if (!opt) return;
    onChange(String(opt.key), opt.text);
    setResolvedName(opt.text);
  };

  const allOptions = value && !options.some(o => o.key === value) && resolvedName
    ? [{ key: value, text: resolvedName }, ...options]
    : options;

  return (
    <ComboBox
      label={label}
      disabled={disabled || !entity}
      placeholder={entity ? "Search records…" : "Pick the destination entity first"}
      allowFreeform={false}
      autoComplete="off"
      selectedKey={value ?? null}
      options={allOptions}
      onChange={onSel}
      onPendingValueChanged={(_, __, val) => { if (val !== undefined) onInputChange(val); }}
      onRenderList={(props, defaultRender) => (
        <>
          {loading && <Spinner size={SpinnerSize.xSmall} label="Searching…" labelPosition="right" styles={{ root: { padding: 8 } }} />}
          {defaultRender?.(props)}
        </>
      )}
    />
  );
};
