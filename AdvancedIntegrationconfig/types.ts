export type EventTypeGuess = "Create" | "Update" | "Delete" | "Manual" | "Display";

export interface AdvancedMapping {
    mfd_advancedmappingid?: string;
    mfd_eventfortable?: string | null;
    mfd_applicationid?: string | null;
    mfd_description?: string | null;
    mfd_eventforsourceentity?: string | null;
    mfd_eventtype?: number | null;
    mfd_destinationentity?: string | null;
    mfd_destinationeventtype?: number | null;
    mfd_enabled?: boolean | null;
    mfd_logusage?: boolean | null;
    mfd_sendnotificaitononerror?: boolean | null;
    mfd_errornotificationteam?: string | null;
    "_mfd_errornotificationteam_value"?: string | null;
    mfd_notificationteamonerrorvia?: string | null;
    mfd_notifyuser?: boolean | null;
    mfd_usernotificationmehtod?: string | null;
    statecode?: number | null;
}

export interface AdvancedMappingSummary {
    id: string;
    name: string;
    applicationId?: string | null;
    enabled?: boolean | null;
}

export interface MappingConditionRow {
    _tempKey?: string;
    mfd_advancedintegrationmappingconditionid?: string;
    "_mfd_advancedintegrationmapping_value"?: string | null;
    mfd_code?: string;
    mfd_operator?: string | null;
    mfd_value?: string | null;
    mfd_destinationvalue?: string | null;
    mfd_comparedatatype?: string | null;
    mfd_valuesourcecolumn?: string | null;
    mfd_destinationsourcecolumn?: string | null;
    mfd_sequence?: number | null;
}

export interface TriggerRow {
    mfd_advancedintegrationtriggerid?: string;
    "_mfd_advancedintegration_value"?: string | null;
    mfd_columnname?: string | null;
    mfd_tablename?: string | null;
    _tempKey?: string;
}

export interface SourceConditionRow {
    mfd_advancedintegrationconditionid?: string;
    "_mfd_advancedinegration_value"?: string | null;
    mfd_code?: string | null;
    mfd_entity?: string | null;
    mfd_field?: string | null;
    mfd_condition?: number | null;
    mfd_value?: string | null;
    statecode?: number | null;
    _tempKey?: string;
}

export interface DestinationConditionRow {
    mfd_advancedintegrationdestinationconditionid?: string;
    "_mfd_advancedintegration_value"?: string | null;
    mfd_code?: string | null;
    mfd_sourceentity?: string | null;
    mfd_sourcecolumn?: string | null;
    mfd_destinationentity?: string | null;
    mfd_destinationcolumn?: string | null;
    mfd_operator?: number | null;
    mfd_usedefaultvalue?: boolean | null;
    mfd_defaultvalue?: string | null;
    mfd_datatype?: number | null;
    statecode?: number | null;
    _tempKey?: string;
}

export interface MappingRow {
    mfd_advancedintegrationmappingid?: string;
    "_mfd_advancedintegration_value"?: string | null;
    mfd_code?: string | null;
    mfd_sourceentity?: string | null;
    mfd_sourcecolumn?: string | null;
    mfd_destinationentity?: string | null;
    mfd_destinationcolumn?: string | null;
    mfd_usedefaultvalue?: boolean | null;
    mfd_defaultvalueifnonefound?: string | null;
    mfd_usecondition?: boolean | null;
    mfd_fieldtype?: number | null;
    mfd_lookupfield?: string | null;
    mfd_userelatedentity?: boolean | null;
    mfd_relatedentityfield?: string | null;
    mfd_conversionformulas?: string | null;
    mfd_ordercolumn?: number | null;
    mfd_description?: string | null;
    mfd_displaynamecolumn?: string | null;
    mfd_width?: number | null;
    _tempKey?: string;
    conditions?: MappingConditionRow[];
}

export interface AdvancedMappingDocument {
    header: AdvancedMapping;
    triggers: TriggerRow[];
    sourceConditions: SourceConditionRow[];
    destinationConditions: DestinationConditionRow[];
    mappings: MappingRow[];
}

export function emptyDocument(): AdvancedMappingDocument {
    return {
        header: { mfd_enabled: true, mfd_logusage: false, mfd_applicationid: "PLUGIN100000" },
        triggers: [],
        sourceConditions: [],
        destinationConditions: [],
        mappings: [],
    };
}

const TYPE_FAMILIES: string[][] = [
    ["String", "Memo", "EntityName"],
    ["Integer", "BigInt", "Decimal", "Double", "Money"],
    ["DateTime"],
    ["Boolean"],
    ["Lookup", "Customer", "Owner", "PartyList"],
    ["Picklist", "State", "Status", "MultiSelectPicklist"],
    ["Uniqueidentifier"],
];

export type TypeCompat = "exact" | "compatible" | "incompatible" | "unknown";

export function compareTypes(sourceType?: string | null, destType?: string | null): TypeCompat {
    if (!sourceType || !destType)
        return "unknown";
    if (sourceType === destType)
        return "exact";
    const famA = TYPE_FAMILIES.find(f => f.includes(sourceType));
    const famB = TYPE_FAMILIES.find(f => f.includes(destType));
    if (famA && famB && famA === famB)
        return "compatible";
    return "incompatible";
}