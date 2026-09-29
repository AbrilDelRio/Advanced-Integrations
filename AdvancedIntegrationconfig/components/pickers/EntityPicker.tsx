import * as React from "react";
import { ComboBox, IComboBoxOption, Spinner, SpinnerSize } from "@fluentui/react";
import { getAllEntities, getEntityDisplayName } from "../../services/metadataService";

export const EntityPicker: React.FC<{
    value?: string | null;
    onChange: (logicalName: string | null) => void;
    label?: string;
    placeholder?: string;
    required?: boolean;
    disabled?: boolean;
}> = ({ value, onChange, label, placeholder, required, disabled }) => {
    const [allOptions, setAllOptions] = React.useState<IComboBoxOption[] | null>(null);
    const [options, setOptions] = React.useState<IComboBoxOption[]>([]);
    const [fallbackText, setFallbackText] = React.useState<string | null>(null);

    React.useEffect(() => {
        let cancelled = false;
        getAllEntities()
            .then(list => {
                if (cancelled) return;
                const opts = list.map(e => ({ 
                    key: e.logicalName, 
                    text: `${e.displayName} (${e.logicalName})` 
                }));
                setAllOptions(opts);
                setOptions(opts);
            })
            .catch(() => { 
                if (!cancelled) {
                    setAllOptions([]);
                    setOptions([]);
                } 
            });
        return () => { cancelled = true; };
    }, []);

    React.useEffect(() => {
        let cancelled = false;
        setFallbackText(null);
        if (!value || !allOptions) return;
        if (allOptions.some(o => o.key === value)) return;

        getEntityDisplayName(value)
            .then(n => { 
                if (!cancelled) {
                    setFallbackText(n === value ? null : n); 
                }
            })
            .catch(() => undefined);

        return () => { cancelled = true; };
    }, [value, allOptions]);

    const applyFilter = (text: string) => {
        if (!allOptions) return;
        const lower = text.trim().toLowerCase();
        if (!lower) {
            setOptions(allOptions);
            return;
        }
        setOptions(allOptions.filter(o => o.text.toLowerCase().includes(lower)));
    };

    if (disabled) {
        const selected = allOptions?.find(o => o.key === value);
        const displayText = selected?.text ?? (value ? (fallbackText ? `${fallbackText} (${value})` : value) : "");
        return (
            <ComboBox 
                label={label} 
                disabled 
                required={required} 
                placeholder={placeholder ?? "(inherited)"} 
                text={displayText} 
                options={value ? [{ key: value, text: displayText }] : []} 
                selectedKey={value ?? null}
            />
        );
    }

    if (allOptions === null) {
        return <Spinner size={SpinnerSize.small} label={label ? `${label}...` : "Loading tables..."} labelPosition="right"/>;
    }

    const selectedOption = allOptions.find(o => o.key === value);
    const knownValue = !value || !!selectedOption;

    const baseOptions = value && !selectedOption 
        ? [{ key: value, text: fallbackText ? `${fallbackText} (${value})` : value }, ...options] 
        : options;

    return (
        <ComboBox 
            label={label} 
            required={required} 
            disabled={disabled} 
            placeholder={placeholder ?? "Search table..."} 
            allowFreeform={true}
            autoComplete="on"
            useComboBoxAsMenuWidth
            selectedKey={value ?? null} 
            text={value && !selectedOption ? (fallbackText ? `${fallbackText} (${value})` : value) : undefined} 
            options={baseOptions} 
            onPendingValueChanged={(_option?: IComboBoxOption, _index?: number, pending?: string) => {
                if (pending !== undefined) {
                    applyFilter(pending);
                }
            }} 
            onChange={(_, option, __, valueTyped) => {
                if (option) {
                    onChange(String(option.key));
                    setOptions(allOptions);
                } else if (valueTyped !== undefined) {
                    // Si el usuario borra por completo el texto o deselecciona
                    if (!valueTyped.trim()) {
                        onChange(null);
                        setOptions(allOptions);
                    }
                }
            }} 
            errorMessage={value && !knownValue && !fallbackText ? "Table not found in current metadata" : undefined}
        />
    );
};