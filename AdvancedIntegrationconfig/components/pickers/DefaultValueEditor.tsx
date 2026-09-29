import * as React from "react";
import { TextField, DatePicker, Toggle, Spinner, SpinnerSize } from "@fluentui/react";
import { AttrMeta, getLookupTarget } from "../../services/metadataService";
import { RecordLookupPicker } from "./RecordLookupPicker";
export const DefaultValueEditor: React.FC<{
    destEntity?: string | null;
    destMeta?: AttrMeta | null;
    value?: string | null;
    onChange: (value: string | null) => void;
    label?: string;
    disabled?: boolean;
}> = ({ destEntity, destMeta, value, onChange, label, disabled }) => {
    const type = destMeta?.attributeType;
    const isLookupType = type === "Lookup" || type === "Customer" || type === "Owner";
    const [target, setTarget] = React.useState<string | null | undefined>(undefined);
    React.useEffect(() => {
        let cancelled = false;
        setTarget(undefined);
        if (!isLookupType || !destEntity || !destMeta?.logicalName)
            return;
        getLookupTarget(destEntity, destMeta.logicalName)
            .then(t => { if (!cancelled)
            setTarget(t); })
            .catch(() => { if (!cancelled)
            setTarget(null); });
        return () => { cancelled = true; };
    }, [isLookupType, destEntity, destMeta?.logicalName]);
    if (isLookupType) {
        if (target === undefined) {
            return <Spinner size={SpinnerSize.small} label={label ? `${label}...` : "Resolving target table..."} labelPosition="right"/>;
        }
        if (!target) {
            return (<TextField label={label ?? "Default value (record GUID)"} value={value ?? ""} disabled={disabled} onChange={(_, v) => onChange(v || null)} description="Target table unknown from metadata - enter the GUID directly."/>);
        }
        return (<RecordLookupPicker entity={target} value={value} disabled={disabled} label={label ?? "Default value"} onChange={(id) => onChange(id)}/>);
    }
    if (type === "DateTime") {
        const dateValue = value ? new Date(value) : undefined;
        return (<DatePicker label={label ?? "Default value"} value={dateValue} disabled={disabled} onSelectDate={(d) => onChange(d ? d.toISOString() : null)} formatDate={(d) => (d ? d.toLocaleDateString() : "")}/>);
    }
    if (type === "Boolean") {
        return (<Toggle label={label ?? "Default value"} disabled={disabled} checked={value === "true" || value === "1"} onChange={(_, checked) => onChange(checked ? "true" : "false")}/>);
    }
    return (<TextField label={label ?? "Default value"} value={value ?? ""} disabled={disabled} onChange={(_, v) => onChange(v || null)}/>);
};
