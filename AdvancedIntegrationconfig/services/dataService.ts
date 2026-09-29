import { AdvancedMapping, AdvancedMappingDocument, AdvancedMappingSummary, TriggerRow, SourceConditionRow, DestinationConditionRow, MappingRow, emptyDocument, } from "../types";
let ctx: ComponentFramework.Context<any> | null = null;
export function registerDataHost(context: ComponentFramework.Context<any>): void {
    ctx = context;
}
function api() {
    if (!ctx)
        throw new Error("dataService: context not registered - call registerDataHost() from index.ts first.");
    return ctx.webAPI;
}
export const ENTITIES = {
    header: "mfd_advancedmapping",
    triggers: "mfd_advancedintegrationtrigger",
    sourceConditions: "mfd_advancedintegrationcondition",
    destinationConditions: "mfd_advancedintegrationdestinationcondition",
    mappings: "mfd_advancedintegrationmapping",
} as const;
export const ENTITY_SETS: Record<keyof typeof ENTITIES, string> = {
    header: "mfd_advancedmappings",
    triggers: "mfd_advancedintegrationtriggers",
    sourceConditions: "mfd_advancedintegrationconditions",
    destinationConditions: "mfd_advancedintegrationdestinationconditions",
    mappings: "mfd_advancedintegrationmappings",
};
const PARENT_LOOKUP_VALUE: Record<"triggers" | "sourceConditions" | "destinationConditions" | "mappings", string> = {
    triggers: "_mfd_advancedintegration_value",
    sourceConditions: "_mfd_advancedinegration_value",
    destinationConditions: "_mfd_advancedintegration_value",
    mappings: "_mfd_advancedintegration_value",
};
const PARENT_NAV_PROPERTY: Record<"triggers" | "sourceConditions" | "destinationConditions" | "mappings", string> = {
    triggers: "mfd_AdvancedIntegration",
    sourceConditions: "mfd_AdvancedInegration",
    destinationConditions: "mfd_AdvancedIntegration",
    mappings: "mfd_AdvancedIntegration",
};
const clean = (id: string): string => (id || "").replace(/[{}]/g, "");
const parentBind = (child: keyof typeof PARENT_NAV_PROPERTY, headerId: string): Record<string, string> => ({
    [`${PARENT_NAV_PROPERTY[child]}@odata.bind`]: `/${ENTITY_SETS.header}(${clean(headerId)})`,
});
const HEADER_SELECT = [
    "mfd_advancedmappingid", "mfd_eventfortable", "mfd_applicationid", "mfd_description",
    "mfd_eventforsourceentity", "mfd_eventtype", "mfd_destinationentity", "mfd_destinationeventtype",
    "mfd_enabled", "mfd_logusage", "mfd_sendnotificaitononerror", "_mfd_errornotificationteam_value",
    "mfd_notificationteamonerrorvia", "mfd_notifyuser", "mfd_usernotificationmehtod", "statecode",
    "_mfd_rununderuser_value",
].join(",");
const TRIGGER_SELECT = [
    "mfd_advancedintegrationtriggerid", "mfd_columnname", "mfd_tablename", "_mfd_advancedintegration_value",
].join(",");
const SOURCE_CONDITION_SELECT = [
    "mfd_advancedintegrationconditionid", "mfd_code", "mfd_entity", "mfd_field", "mfd_condition",
    "mfd_value", "statecode", "_mfd_advancedinegration_value",
].join(",");
const DESTINATION_CONDITION_SELECT = [
    "mfd_advancedintegrationdestinationconditionid", "mfd_code", "mfd_sourceentity", "mfd_sourcecolumn",
    "mfd_destinationentity", "mfd_destinationcolumn", "mfd_operator", "mfd_usedefaultvalue",
    "mfd_defaultvalue", "mfd_datatype", "statecode", "_mfd_advancedintegration_value",
].join(",");
const MAPPING_SELECT = [
    "mfd_advancedintegrationmappingid", "mfd_code", "mfd_sourceentity", "mfd_sourcecolumn",
    "mfd_destinationentity", "mfd_destinationcolumn", "mfd_usedefaultvalue", "mfd_defaultvalueifnonefound",
    "mfd_usecondition", "mfd_fieldtype", "mfd_lookupfield", "mfd_userelatedentity", "mfd_relatedentityfield",
    "mfd_conversionformulas", "mfd_ordercolumn", "mfd_description", "mfd_displaynamecolumn", "mfd_width",
    "_mfd_advancedintegration_value",
].join(",");
const HEADER_WRITABLE = [
    "mfd_applicationid", "mfd_description", "mfd_eventforsourceentity", "mfd_eventtype",
    "mfd_destinationentity", "mfd_destinationeventtype", "mfd_enabled", "mfd_logusage",
    "mfd_sendnotificaitononerror", "mfd_notificationteamonerrorvia", "mfd_notifyuser", "mfd_usernotificationmehtod",
];
const TRIGGER_WRITABLE = ["mfd_columnname", "mfd_tablename"];
const SOURCE_CONDITION_WRITABLE = ["mfd_entity", "mfd_field", "mfd_condition", "mfd_value"];
const DESTINATION_CONDITION_WRITABLE = [
    "mfd_sourceentity", "mfd_sourcecolumn", "mfd_destinationentity", "mfd_destinationcolumn",
    "mfd_operator", "mfd_usedefaultvalue", "mfd_defaultvalue", "mfd_datatype",
];
const MAPPING_WRITABLE = [
    "mfd_sourceentity", "mfd_sourcecolumn", "mfd_destinationentity", "mfd_destinationcolumn",
    "mfd_usedefaultvalue", "mfd_defaultvalueifnonefound", "mfd_usecondition", "mfd_fieldtype",
    "mfd_lookupfield", "mfd_userelatedentity", "mfd_relatedentityfield", "mfd_conversionformulas",
    "mfd_ordercolumn", "mfd_description", "mfd_displaynamecolumn", "mfd_width",
];
function pick(row: Record<string, any>, fields: string[]): Record<string, unknown> {
    const out: Record<string, unknown> = {};
    for (const f of fields) {
        if (f in row)
            out[f] = row[f];
    }
    return out;
}
export async function listMappings(): Promise<AdvancedMappingSummary[]> {
    const res = await api().retrieveMultipleRecords(ENTITIES.header, "?$select=mfd_advancedmappingid,mfd_eventfortable,mfd_applicationid,mfd_enabled&$orderby=mfd_eventfortable");
    return (res.entities ?? []).map((e: any) => ({
        id: e.mfd_advancedmappingid,
        name: e.mfd_eventfortable || "(unnamed)",
        applicationId: e.mfd_applicationid,
        enabled: e.mfd_enabled,
    }));
}
export async function loadDocument(headerId: string): Promise<AdvancedMappingDocument> {
    const id = clean(headerId);
    const filterFor = (child: keyof typeof PARENT_LOOKUP_VALUE) => `&$filter=${PARENT_LOOKUP_VALUE[child]} eq ${id}`;
    const [header, triggers, sourceConditions, destinationConditions, mappings] = await Promise.all([
        api().retrieveRecord(ENTITIES.header, id, `?$select=${HEADER_SELECT}`),
        api().retrieveMultipleRecords(ENTITIES.triggers, `?$select=${TRIGGER_SELECT}${filterFor("triggers")}`).then(r => r.entities ?? []),
        api().retrieveMultipleRecords(ENTITIES.sourceConditions, `?$select=${SOURCE_CONDITION_SELECT}${filterFor("sourceConditions")}`).then(r => r.entities ?? []),
        api().retrieveMultipleRecords(ENTITIES.destinationConditions, `?$select=${DESTINATION_CONDITION_SELECT}${filterFor("destinationConditions")}`).then(r => r.entities ?? []),
        api().retrieveMultipleRecords(ENTITIES.mappings, `?$select=${MAPPING_SELECT}${filterFor("mappings")}&$orderby=mfd_ordercolumn`).then(r => r.entities ?? []),
    ]);
    return {
        header: {
            ...(header as AdvancedMapping),
            mfd_errornotificationteam: (header as any)._mfd_errornotificationteam_value ?? null,
            mfd_rununderuser: (header as any)._mfd_rununderuser_value ?? null,
        },
        triggers: triggers as TriggerRow[],
        sourceConditions: sourceConditions as SourceConditionRow[],
        destinationConditions: destinationConditions as DestinationConditionRow[],
        mappings: mappings as MappingRow[],
    };
}
export function newDocument(): AdvancedMappingDocument {
    return emptyDocument();
}
function buildHeaderPayload(h: AdvancedMapping): Record<string, unknown> {
    const out = pick(h as unknown as Record<string, any>, HEADER_WRITABLE);
    if (h.mfd_errornotificationteam) {
        out["mfd_ErrorNotificationTeam@odata.bind"] = `/teams(${clean(h.mfd_errornotificationteam)})`;
    }
    else if (h.mfd_errornotificationteam === null) {
        out["mfd_ErrorNotificationTeam@odata.bind"] = null;
    }
    // Run Under User -> AutoRegisterPlugin copies it to the step's impersonatinguserid.
    // undefined = not touched (new record), null = cleared (Calling User).
    if (h.mfd_rununderuser) {
        out["mfd_RunUnderUser@odata.bind"] = `/systemusers(${clean(h.mfd_rununderuser)})`;
    }
    else if (h.mfd_rununderuser === null) {
        out["mfd_RunUnderUser@odata.bind"] = null;
    }
    return out;
}
export async function createHeader(header: AdvancedMapping): Promise<string> {
    const r = await api().createRecord(ENTITIES.header, buildHeaderPayload(header));
    return clean(r.id);
}
export async function updateHeader(id: string, header: AdvancedMapping): Promise<void> {
    await api().updateRecord(ENTITIES.header, clean(id), buildHeaderPayload(header));
}
async function saveChildCollection<T extends Record<string, any>>(child: keyof typeof PARENT_NAV_PROPERTY, writableFields: string[], entityLogicalName: string, idField: string, headerId: string, current: T[], original: T[]): Promise<T[]> {
    const originalIds = new Set(original.map(r => r[idField]).filter(Boolean));
    const currentIds = new Set(current.map(r => r[idField]).filter(Boolean));
    const toDelete = [...originalIds].filter(id => !currentIds.has(id));
    await Promise.all(toDelete.map(id => api().deleteRecord(entityLogicalName, clean(id as string))));
    const saved: T[] = [];
    for (const row of current) {
        const id = row[idField];
        const payload: Record<string, unknown> = { ...pick(row, writableFields), ...parentBind(child, headerId) };
        if (id) {
            await api().updateRecord(entityLogicalName, clean(id), payload);
            saved.push({ ...row, [idField]: id });
        }
        else {
            const r = await api().createRecord(entityLogicalName, payload);
            saved.push({ ...row, [idField]: clean(r.id) });
        }
    }
    return saved;
}
export interface SaveResult {
    headerId: string;
    document: AdvancedMappingDocument;
}
export async function saveDocument(doc: AdvancedMappingDocument, original: AdvancedMappingDocument | null): Promise<SaveResult> {
    let headerId = doc.header.mfd_advancedmappingid ?? "";
    if (headerId) {
        await updateHeader(headerId, doc.header);
    }
    else {
        headerId = await createHeader(doc.header);
    }
    const [triggers, sourceConditions, destinationConditions, mappings] = await Promise.all([
        saveChildCollection("triggers", TRIGGER_WRITABLE, ENTITIES.triggers, "mfd_advancedintegrationtriggerid", headerId, doc.triggers, original?.triggers ?? []),
        saveChildCollection("sourceConditions", SOURCE_CONDITION_WRITABLE, ENTITIES.sourceConditions, "mfd_advancedintegrationconditionid", headerId, doc.sourceConditions, original?.sourceConditions ?? []),
        saveChildCollection("destinationConditions", DESTINATION_CONDITION_WRITABLE, ENTITIES.destinationConditions, "mfd_advancedintegrationdestinationconditionid", headerId, doc.destinationConditions, original?.destinationConditions ?? []),
        saveChildCollection("mappings", MAPPING_WRITABLE, ENTITIES.mappings, "mfd_advancedintegrationmappingid", headerId, doc.mappings, original?.mappings ?? []),
    ]);
    const savedHeader = { ...doc.header, mfd_advancedmappingid: headerId };
    return {
        headerId,
        document: { header: savedHeader, triggers, sourceConditions, destinationConditions, mappings },
    };
}
export async function deleteMapping(headerId: string): Promise<void> {
    const doc = await loadDocument(headerId);
    await Promise.all([
        ...doc.triggers.map(t => api().deleteRecord(ENTITIES.triggers, clean(t.mfd_advancedintegrationtriggerid!))),
        ...doc.sourceConditions.map(c => api().deleteRecord(ENTITIES.sourceConditions, clean(c.mfd_advancedintegrationconditionid!))),
        ...doc.destinationConditions.map(c => api().deleteRecord(ENTITIES.destinationConditions, clean(c.mfd_advancedintegrationdestinationconditionid!))),
        ...doc.mappings.map(m => api().deleteRecord(ENTITIES.mappings, clean(m.mfd_advancedintegrationmappingid!))),
    ]);
    await api().deleteRecord(ENTITIES.header, clean(headerId));
}
