let ctx: ComponentFramework.Context<any> | null = null;
export function registerMetadataHost(context: ComponentFramework.Context<any>): void {
    ctx = context;
}
function webApi() {
    if (!ctx)
        throw new Error("metadataService: context not registered - call registerMetadataHost() from index.ts first.");
    return ctx.webAPI;
}
function utils(): any {
    return ctx?.utils as any;
}
const API_VERSION = "v9.1";
function getClientUrl(): string | null {
    const xrm = (window as any).Xrm;
    try {
        const url = xrm?.Utility?.getGlobalContext?.()?.getClientUrl?.();
        return typeof url === "string" && url ? url : null;
    }
    catch {
        return null;
    }
}
async function restFetch(url: string): Promise<any> {
    const resp = await fetch(url, {
        method: "GET",
        headers: {
            "OData-Version": "4.0",
            "OData-MaxVersion": "4.0",
            "Accept": "application/json",
        },
    });
    if (!resp.ok)
        throw new Error(await resp.text());
    return resp.json();
}
const mem = new Map<string, unknown>();
function cached<T>(key: string, loader: () => Promise<T>): Promise<T> {
    if (mem.has(key))
        return Promise.resolve(mem.get(key) as T);
    return loader().then(v => { mem.set(key, v); return v; });
}
const rawAttrs = new Map<string, Map<string, any>>();
const fullLoad = new Map<string, boolean>();
const lastError = new Map<string, string>();
const entitySetCache = new Map<string, string>();
export function clearMetadataCache(): void {
    mem.clear();
    rawAttrs.clear();
    fullLoad.clear();
    lastError.clear();
    entitySetCache.clear();
}
export interface AttrMeta {
    logicalName: string;
    displayName: string;
    attributeType: string;
    isValidForRead: boolean;
    isValidForCreate: boolean;
    isValidForUpdate: boolean;
    isPrimaryName?: boolean;
    targets?: string[];
}
export interface RelationshipMeta {
    schemaName: string;
    referencingAttribute: string;
    referencedEntity: string;
    referencedEntityDisplay: string;
    navProperty: string;
}
export interface OptionMeta {
    value: number;
    label: string;
    color?: string | null;
}
export interface TeamMeta {
    id: string;
    name: string;
}
export interface MetadataStatus {
    attributeCount: number;
    fullyLoaded: boolean;
    error: string | null;
}
function toArray(coll: any): any[] {
    if (!coll)
        return [];
    if (Array.isArray(coll))
        return coll;
    if (typeof coll.getAll === "function") {
        try {
            const a = coll.getAll();
            if (Array.isArray(a) && a.length)
                return a;
        }
        catch { }
    }
    if (typeof coll.getLength === "function") {
        try {
            const n = coll.getLength();
            const pick = typeof coll.getByIndex === "function" ? coll.getByIndex.bind(coll) : coll.get?.bind(coll);
            if (typeof pick === "function" && n > 0) {
                const out: any[] = [];
                for (let i = 0; i < n; i++) {
                    const it = pick(i);
                    if (it)
                        out.push(it);
                }
                if (out.length)
                    return out;
            }
        }
        catch { }
    }
    if (typeof coll.forEach === "function") {
        try {
            const out: any[] = [];
            coll.forEach((v: any) => out.push(v));
            if (out.length)
                return out;
        }
        catch { }
    }
    if (Array.isArray(coll.value))
        return coll.value;
    return Object.values(coll).filter(v => v && typeof v === "object");
}
const label = (v: any): string | undefined => typeof v === "string" ? v : (v?.UserLocalizedLabel?.Label ?? v?.LocalizedLabels?.[0]?.Label);
const bool = (v: any, dflt: boolean): boolean => {
    const x = v?.Value ?? v;
    return x === undefined || x === null ? dflt : !!x;
};
const TYPE_CODES: Record<number, string> = {
    0: "Boolean", 1: "Customer", 2: "DateTime", 3: "Decimal", 4: "Double", 5: "Integer",
    6: "Lookup", 7: "Memo", 8: "Money", 9: "Owner", 10: "PartyList", 11: "Picklist",
    12: "State", 13: "Status", 14: "String", 15: "Uniqueidentifier", 16: "CalendarRules",
    17: "Virtual", 18: "BigInt", 19: "ManagedProperty", 20: "EntityName",
};
function typeName(a: any): string {
    const t = a?.AttributeType ?? a?.AttributeTypeName?.Value ?? a?.AttributeTypeName;
    if (typeof t === "number")
        return TYPE_CODES[t] ?? String(t);
    const s = String(t ?? "");
    if (s === "MultiSelectPicklistType")
        return "MultiSelectPicklist";
    return s.endsWith("Type") && s.length > 4 ? s.slice(0, -4) : s;
}
function toAttrMeta(a: any): AttrMeta {
    return {
        logicalName: a.LogicalName,
        displayName: label(a.DisplayName) || a.LogicalName,
        attributeType: typeName(a),
        isValidForRead: bool(a.IsValidForRead, true),
        isValidForCreate: bool(a.IsValidForCreate, true),
        isValidForUpdate: bool(a.IsValidForUpdate, true),
        isPrimaryName: !!a.IsPrimaryName,
        targets: a.Targets,
    };
}
const ATTR_SELECT = "LogicalName,DisplayName,AttributeType,IsValidForCreate,IsValidForUpdate,IsValidForRead,IsPrimaryName";
async function fetchEntityDefinitionViaRest(entity: string, attributes?: string[]): Promise<any | null> {
    const base = getClientUrl();
    if (!base)
        return null;
    const nameFilter = attributes && attributes.length
        ? ` and (${attributes.map(a => `LogicalName eq '${a.replace(/'/g, "''")}'`).join(" or ")})`
        : "";
    const attrFilter = `AttributeOf eq null${nameFilter}`;
    const url = `${base}/api/data/${API_VERSION}/EntityDefinitions(LogicalName='${encodeURIComponent(entity.toLowerCase())}')` +
        `?$select=LogicalName,DisplayName,EntitySetName&$expand=Attributes($select=${ATTR_SELECT};$filter=${attrFilter})`;
    return restFetch(url);
}
async function fetchEntityDefinitionViaLegacyApi(entity: string, attributes?: string[]): Promise<any | null> {
    const providers: {
        name: string;
        fn: () => Promise<any>;
    }[] = [];
    const u = utils();
    if (typeof u?.getEntityMetadata === "function") {
        providers.push({ name: "context.utils", fn: () => u.getEntityMetadata(entity, attributes ?? []) });
    }
    const xrmUtil = (window as any).Xrm?.Utility;
    if (typeof xrmUtil?.getEntityMetadata === "function") {
        providers.push({ name: "Xrm.Utility", fn: () => xrmUtil.getEntityMetadata(entity, attributes ?? []) });
    }
    for (const p of providers) {
        try {
            const m = await p.fn();
            if (m)
                return m;
        }
        catch { }
    }
    return null;
}
async function callProviders(entity: string, attributes?: string[]): Promise<any | null> {
    const errors: string[] = [];
    try {
        const m = await fetchEntityDefinitionViaRest(entity, attributes);
        if (m)
            return m;
        errors.push("REST EntityDefinitions: empty result (or Xrm.Utility.getGlobalContext unavailable)");
    }
    catch (e) {
        errors.push("REST EntityDefinitions: " + ((e as Error)?.message ?? String(e)));
    }
    try {
        const m = await fetchEntityDefinitionViaLegacyApi(entity, attributes);
        if (m)
            return m;
        errors.push("getEntityMetadata: empty result");
    }
    catch (e) {
        errors.push("getEntityMetadata: " + ((e as Error)?.message ?? String(e)));
    }
    lastError.set(entity, errors.join(" - "));
    return null;
}
function store(entity: string): Map<string, any> {
    let m = rawAttrs.get(entity);
    if (!m) {
        m = new Map();
        rawAttrs.set(entity, m);
    }
    return m;
}
function merge(entity: string, meta: any): number {
    if (meta?.EntitySetName)
        entitySetCache.set(entity, meta.EntitySetName);
    const list = toArray(meta?.Attributes).filter(a => a && a.LogicalName);
    const target = store(entity);
    for (const a of list)
        target.set(a.LogicalName, a);
    return list.length;
}
const fullInflight = new Map<string, Promise<void>>();
function loadAll(entity: string): Promise<void> {
    if (fullLoad.get(entity))
        return Promise.resolve();
    const running = fullInflight.get(entity);
    if (running)
        return running;
    const p = (async () => {
        const meta = await callProviders(entity);
        if (merge(entity, meta) > 0) {
            fullLoad.set(entity, true);
            lastError.delete(entity);
        }
        else if (meta && !lastError.has(entity)) {
            lastError.set(entity, `Metadata for "${entity}" came back without attributes.`);
        }
    })().finally(() => fullInflight.delete(entity));
    fullInflight.set(entity, p);
    return p;
}
export async function getAttributes(entity: string): Promise<AttrMeta[]> {
    if (!entity)
        return [];
    await loadAll(entity);
    return [...store(entity).values()]
        .filter(a => typeName(a) !== "Virtual")
        .map(toAttrMeta)
        .sort((x, y) => x.displayName.localeCompare(y.displayName));
}
export async function getAttribute(entity: string, attribute: string): Promise<AttrMeta | null> {
    if (!entity || !attribute)
        return null;
    const map = await ensureAttributes(entity, [attribute]);
    return map.get(attribute) ?? null;
}
export async function ensureAttributes(entity: string, names: string[]): Promise<Map<string, AttrMeta>> {
    await loadAll(entity);
    const target = store(entity);
    const missing = names.filter(n => n && !target.has(n));
    if (missing.length > 0) {
        const meta = await callProviders(entity, missing);
        if (merge(entity, meta) > 0)
            lastError.delete(entity);
    }
    const out = new Map<string, AttrMeta>();
    for (const n of names) {
        if (!n)
            continue;
        const a = target.get(n);
        if (a)
            out.set(n, toAttrMeta(a));
    }
    return out;
}
export function getMetadataStatus(entity: string): MetadataStatus {
    return {
        attributeCount: store(entity).size,
        fullyLoaded: !!fullLoad.get(entity),
        error: lastError.get(entity) ?? null,
    };
}
export async function getEntitySetName(entity: string): Promise<string> {
    if (entitySetCache.has(entity))
        return entitySetCache.get(entity)!;
    await loadAll(entity);
    return entitySetCache.get(entity) ?? `${entity}s`;
}
export function getEntityDisplayName(entity: string): Promise<string> {
    return cached("entitydisplay:" + entity, async () => {
        const meta = await callProviders(entity);
        return (meta && label(meta.DisplayName)) || entity;
    });
}
export interface EntityListItem {
    logicalName: string;
    displayName: string;
}
export function getAllEntities(): Promise<EntityListItem[]> {
    return cached("allentities", async () => {
        const base = getClientUrl();
        if (!base)
            return [];
        const url = `${base}/api/data/${API_VERSION}/EntityDefinitions?$select=LogicalName,DisplayName,MetadataId`;
        const data = await restFetch(url);
        const rows = (data?.value ?? []) as any[];
        return rows
            .map(e => ({ logicalName: e.LogicalName, displayName: label(e.DisplayName) || e.LogicalName }))
            .sort((a, b) => a.displayName.localeCompare(b.displayName));
    });
}
async function getLookupTargetsViaRest(entity: string, attribute: string): Promise<string[] | null> {
    const base = getClientUrl();
    if (!base)
        return null;
    const url = `${base}/api/data/${API_VERSION}/EntityDefinitions(LogicalName='${encodeURIComponent(entity.toLowerCase())}')` +
        `/Attributes(LogicalName='${encodeURIComponent(attribute)}')/Microsoft.Dynamics.CRM.LookupAttributeMetadata?$select=Targets`;
    const data = await restFetch(url);
    return Array.isArray(data?.Targets) ? data.Targets : null;
}
export function getLookupTarget(entity: string, attribute: string): Promise<string | null> {
    return cached("lookuptarget:" + entity + ":" + attribute, async () => {
        try {
            const targets = await getLookupTargetsViaRest(entity, attribute);
            if (targets && targets.length)
                return targets[0];
        }
        catch { }
        const attr = await getAttribute(entity, attribute);
        return attr?.targets?.[0] ?? null;
    });
}
function optionsFromAttr(attr: any): OptionMeta[] {
    const raw = attr?.OptionSet;
    const list: any[] = Array.isArray(raw) ? raw
        : Array.isArray(raw?.Options) ? raw.Options
            : toArray(raw?.Options ?? raw);
    return list
        .filter(o => o && o.Value !== undefined && o.Value !== null)
        .map<OptionMeta>(o => ({ value: Number(o.Value), label: label(o.Label) || String(o.Value), color: o.Color ?? null }));
}
const OPTIONSET_CAST: Record<string, string> = {
    Picklist: "Microsoft.Dynamics.CRM.PicklistAttributeMetadata",
    State: "Microsoft.Dynamics.CRM.StateAttributeMetadata",
    Status: "Microsoft.Dynamics.CRM.StatusAttributeMetadata",
    MultiSelectPicklist: "Microsoft.Dynamics.CRM.MultiSelectPicklistAttributeMetadata",
    Boolean: "Microsoft.Dynamics.CRM.BooleanAttributeMetadata",
};
async function fetchOptionSetViaRest(entity: string, attribute: string, attrType: string): Promise<any | null> {
    const cast = OPTIONSET_CAST[attrType];
    const base = getClientUrl();
    if (!cast || !base)
        return null;
    const url = `${base}/api/data/${API_VERSION}/EntityDefinitions(LogicalName='${encodeURIComponent(entity.toLowerCase())}')` +
        `/Attributes(LogicalName='${encodeURIComponent(attribute)}')/${cast}?$select=LogicalName&$expand=OptionSet`;
    return restFetch(url);
}
async function fetchOptionSetViaLegacyApi(entity: string, attribute: string): Promise<any | null> {
    const meta = await fetchEntityDefinitionViaLegacyApi(entity, [attribute]);
    const first = toArray(meta?.Attributes)[0];
    return first ?? null;
}
export function getOptionSet(entity: string, attribute: string): Promise<OptionMeta[]> {
    return cached("opt:" + entity + ":" + attribute, async () => {
        const attrMeta = await getAttribute(entity, attribute);
        const attrType = attrMeta?.attributeType;
        if (attrType && OPTIONSET_CAST[attrType]) {
            try {
                const raw = await fetchOptionSetViaRest(entity, attribute, attrType);
                if (raw) {
                    if (attrType === "Boolean") {
                        const opt = raw.OptionSet;
                        const t = opt?.TrueOption, f = opt?.FalseOption;
                        if (t && f) {
                            return [
                                { value: Number(f.Value), label: label(f.Label) || "No" },
                                { value: Number(t.Value), label: label(t.Label) || "Yes" },
                            ];
                        }
                    }
                    else {
                        const opts = optionsFromAttr(raw);
                        if (opts.length)
                            return opts;
                    }
                }
            }
            catch { }
        }
        try {
            const raw = await fetchOptionSetViaLegacyApi(entity, attribute);
            if (raw && typeName(raw) === "Boolean") {
                const opt = raw.OptionSet;
                const t = opt?.TrueOption, f = opt?.FalseOption;
                if (t && f) {
                    return [
                        { value: Number(f.Value), label: label(f.Label) || "No" },
                        { value: Number(t.Value), label: label(t.Label) || "Yes" },
                    ];
                }
            }
            return optionsFromAttr(raw);
        }
        catch {
            return [];
        }
    });
}
export function getManyToOneRelationships(entity: string): Promise<RelationshipMeta[]> {
    return cached("m2o:" + entity, async () => {
        const meta = await callProviders(entity);
        const rels = toArray(meta?.ManyToOneRelationships)
            .filter(r => r && r.ReferencedEntity && r.ReferencingAttribute);
        const referenced = [...new Set<string>(rels.map(r => r.ReferencedEntity))];
        const nameMap = new Map<string, string>();
        await Promise.all(referenced.map(async (e) => { nameMap.set(e, await getEntityDisplayName(e)); }));
        return rels
            .map<RelationshipMeta>(r => ({
            schemaName: r.SchemaName,
            referencingAttribute: r.ReferencingAttribute,
            referencedEntity: r.ReferencedEntity,
            referencedEntityDisplay: nameMap.get(r.ReferencedEntity) ?? r.ReferencedEntity,
            navProperty: r.ReferencingEntityNavigationPropertyName ?? r.ReferencingAttribute,
        }))
            .sort((a, b) => a.referencedEntityDisplay.localeCompare(b.referencedEntityDisplay));
    });
}
export function getTeams(): Promise<TeamMeta[]> {
    return cached("teams", async () => {
        const res = await webApi().retrieveMultipleRecords("team", "?$select=teamid,name&$orderby=name&$top=500").catch(() => ({ entities: [] as any[] }));
        return (res.entities ?? []).map((t: any) => ({ id: t.teamid, name: t.name }));
    });
}
export async function searchRecords(entity: string, searchText: string, top = 25): Promise<{
    id: string;
    name: string;
}[]> {
    const attrs = await getAttributes(entity);
    const primary = attrs.find(a => a.isPrimaryName)?.logicalName ?? "name";
    const idField = entity + "id";
    const filter = searchText ? `&$filter=contains(${primary}, '${searchText.replace(/'/g, "''")}')` : "";
    const res = await webApi()
        .retrieveMultipleRecords(entity, `?$select=${idField},${primary}${filter}&$top=${top}&$orderby=${primary}`)
        .catch(() => ({ entities: [] as any[] }));
    return (res.entities ?? []).map((r: any) => ({ id: r[idField], name: r[primary] ?? "(unnamed)" }));
}
export async function getRecordName(entity: string, id: string): Promise<string | null> {
    if (!id)
        return null;
    const attrs = await getAttributes(entity);
    const primary = attrs.find(a => a.isPrimaryName)?.logicalName ?? "name";
    try {
        const rec = await webApi().retrieveRecord(entity, id.replace(/[{}]/g, ""), `?$select=${primary}`);
        return rec?.[primary] ?? null;
    }
    catch {
        return null;
    }
}
