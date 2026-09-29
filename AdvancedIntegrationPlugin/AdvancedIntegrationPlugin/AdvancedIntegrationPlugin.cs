using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace CreateAdvancedIntegrationPlugin
{
    public class CreateAdvancedIntegrationPlugin : IPlugin
    {
        private readonly string _unsecureConfig;
        private readonly string _secureConfig;

        private const string VALID_APPLICATION_ID = "PLUGIN100000";
        private const string TRIGGER_TABLE = "mfd_advancedintegrationtrigger";

        private const int DEST_EVENT_CREATE = 1;
        private const int DEST_EVENT_UPDATE = 2;
        private const int DEST_EVENT_DELETE = 3;

        private const int OPERATOR_EQUAL = 1;
        private const int OPERATOR_NOT_EQUAL = 2;
        private const int OPERATOR_CONTAINS = 3;
        private const int OPERATOR_GREATER_THAN = 4;
        private const int OPERATOR_LESS_THAN = 5;
        private const int OPERATOR_IS_NULL = 6;
        private const int OPERATOR_IS_NOT_NULL = 7;

        private const int DATATYPE_STRING = 1;
        private const int DATATYPE_INTEGER = 2;
        private const int DATATYPE_GUID = 3;
        private const int DATATYPE_DECIMAL = 4;
        private const int DATATYPE_BOOLEAN = 5;
        private const int DATATYPE_DATETIME = 6;

        private const int NOTIFY_IN_APP = 1;
        private const int NOTIFY_TEAMS = 2;
        private const int NOTIFY_EMAIL = 3;

        private const int ICON_INFO = 100000000;
        private const int ICON_SUCCESS = 100000001;
        private const int ICON_FAILURE = 100000002;
        private const int ICON_WARNING = 100000003;

        private const int TOAST_TIMED = 200000000;
        private const int TOAST_HIDDEN = 200000001;

        private const int MAX_RECORDS_PER_BATCH = 1000;
        private const int MAX_TOTAL_RECORDS = 5000;
        private const int MAX_URLS_IN_LOG = 20;

        public CreateAdvancedIntegrationPlugin() : this(null, null) { }
        public CreateAdvancedIntegrationPlugin(string unsecureConfig, string secureConfig)
        {
            _unsecureConfig = unsecureConfig;
            _secureConfig = secureConfig;
        }

        private const int MAX_SAFE_DEPTH = 7;

        public void Execute(IServiceProvider serviceProvider)
        {
            IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            if (context.Depth > MAX_SAFE_DEPTH)
                return;
            string loopKey = $"AdvInt_{context.OwningExtension.Id}_{context.PrimaryEntityName}_{context.PrimaryEntityId}_{context.MessageName}";
            var rootContext = GetRootContext(context);
            if (rootContext.SharedVariables.ContainsKey(loopKey))
                return;

            rootContext.SharedVariables[loopKey] = true;

            IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);

            var errorLog = new StringBuilder();
            Entity mappingRecord = null;
            bool wasSuccessful = false;
            bool hasErrors = false;
            string sourceRecordUrl = null;
            string environmentUrl = null;
            int recordsAffected = 0;
            string destinationEntityName = null;
            List<Guid> affectedRecordIds = new List<Guid>();
            Guid? logRecordId = null;
            bool isEnabled = true;

            // [PERF] Metadata cache scoped to this single Execute() call - avoids one
            // uncached RetrieveAttributeRequest per field, per mapping/condition, on every
            // single execution regardless of outcome (see ConvertToSafeType). Declared
            // outside the try block so it's available to the finally block too if ever needed.
            var metadataCache = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity targetEntity))
                    return;
                const string ORIGIN_FIELD = "ngmm_integrationchangeorigincode";
                const string SAP_ORIGIN_VALUE = "SAP";
                var sapLoopEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "msdyn_workorder",
                    "msdyn_projecttask"
                };

                if (sapLoopEntities.Contains(context.PrimaryEntityName))
                {
                    string changeOrigin = null;
                    if (targetEntity.Contains(ORIGIN_FIELD))
                        changeOrigin = targetEntity.GetAttributeValue<string>(ORIGIN_FIELD);

                    if (string.IsNullOrEmpty(changeOrigin))
                    {
                        Entity preImageForOrigin = context.PreEntityImages.Contains("PreImage") ? context.PreEntityImages["PreImage"] : null;
                        if (preImageForOrigin != null && preImageForOrigin.Contains(ORIGIN_FIELD))
                            changeOrigin = preImageForOrigin.GetAttributeValue<string>(ORIGIN_FIELD);
                    }

                    if (string.Equals(changeOrigin, SAP_ORIGIN_VALUE, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                string sourceEntityName = context.PrimaryEntityName;
                Entity preImage = context.PreEntityImages.Contains("PreImage") ? context.PreEntityImages["PreImage"] : null;

                // [PERF: TARGETED COLUMNSET] mappingRecord, isEnabled, the field mappings and
                // mapping conditions are all resolved BEFORE the source record is retrieved
                // below. That lets us ask for only the columns this run can possibly need,
                // instead of `new ColumnSet(true)` (every column, on every Update) - confirmed
                // via Plugin Trace Log as the main driver of the 2-minute plugin timeouts
                // (BUG/telemetry finding on CreateAdvancedIntegrationPlugin).
                mappingRecord = GetMappingRecord(service, context, errorLog);
                if (mappingRecord == null)
                {
                    hasErrors = true;
                    errorLog.AppendLine("Mapping record not found");
                    return;
                }

                isEnabled = mappingRecord.GetAttributeValue<bool?>("mfd_enabled") ?? true;
                if (!isEnabled)
                    return;

                // ===========================================================================
                // [CHANGE DETECTION]
                // Bloquea la ejecucion cuando ningun campo trigger cambio realmente de valor.
                //
                // Por que hace falta: los filtering attributes del step filtran por PRESENCIA
                // del campo en el payload, no por cambio de valor. Si SAP (o un Flow, u otro
                // plugin) reescribe el mismo valor, el step se dispara igual y la integracion
                // corre de nuevo. Dataverse NO audita esas escrituras porque no cambian nada,
                // de ahi el sintoma tipico: 1 update en el audit, 2 logs.
                //
                // Un registro en mfd_advancedintegrationtrigger significa "disparar cuando ESTE
                // campo cambie". Que se dispare sin cambio es el bug, no una funcionalidad,
                // por eso va siempre activo y sin flag de configuracion.
                //
                // Fail-open en todos los caminos (sin triggers, sin PreImage, query con error):
                // procede y deja constancia en el log. Nunca corta en silencio.
                //
                // Los diagnosticos se escriben en AMBOS caminos (skip y proceed) a proposito:
                // para saber que genera la doble llamada hay que poder comparar el log que
                // se bloqueo contra el que paso.
                // ===========================================================================
                if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    var triggerFields = GetTriggerColumns(service, mappingRecord.Id, errorLog);

                    if (!triggerFields.Any())
                    {
                        errorLog.AppendLine("WARNING: no trigger records found for this mapping. Change detection skipped, proceeding.");
                    }
                    else if (preImage == null)
                    {
                        errorLog.AppendLine("WARNING: no PreImage registered on this step. Change detection skipped, proceeding.");
                    }
                    else
                    {
                        AppendCallDiagnostics(context, targetEntity, errorLog);

                        bool anyChanged = false;
                        var changedFields = new List<string>();

                        errorLog.AppendLine("--- TRIGGER FIELDS ---");
                        foreach (var field in triggerFields)
                        {
                            object oldValue = preImage.Contains(field) ? preImage[field] : null;

                            if (!targetEntity.Contains(field))
                            {
                                errorLog.AppendLine($"  {field}: not in payload (current value: {FormatValueForLog(oldValue)})");
                                continue;
                            }

                            object newValue = targetEntity[field];
                            bool changed = HasValueChanged(oldValue, newValue);

                            errorLog.AppendLine($"  {field}: {FormatValueForLog(oldValue)} -> {FormatValueForLog(newValue)}  [{(changed ? "CHANGED" : "same value")}]");

                            if (changed)
                            {
                                anyChanged = true;
                                changedFields.Add(field);
                            }
                        }
                        errorLog.AppendLine("----------------------");

                        if (!anyChanged)
                        {
                            errorLog.AppendLine("RESULT: no trigger field changed value. Execution SKIPPED (duplicate / no-op write).");
                            errorLog.AppendLine("This write re-sent the same values that were already in the record.");
                            errorLog.AppendLine("Compare CorrelationId with the other log for this record:");
                            errorLog.AppendLine("  same CorrelationId      -> one inbound call that cascaded inside Dataverse (Flow / plugin / workflow).");
                            errorLog.AppendLine("  different CorrelationId  -> two separate inbound requests (the caller sent it twice).");
                            wasSuccessful = true;
                            return;
                        }

                        errorLog.AppendLine($"RESULT: trigger field(s) changed ({string.Join(", ", changedFields)}). Proceeding.");
                    }
                }

                destinationEntityName = mappingRecord.GetAttributeValue<string>("mfd_destinationentity");
                if (string.IsNullOrWhiteSpace(destinationEntityName))
                {
                    hasErrors = true;
                    errorLog.AppendLine("Destination entity is empty");
                    return;
                }

                int destEventType = mappingRecord.GetAttributeValue<OptionSetValue>("mfd_destinationeventtype")?.Value ?? DEST_EVENT_CREATE;

                var fieldMappings = GetFieldMappings(service, mappingRecord.Id, errorLog);

                var allMappingConditions = GetAllMappingConditions(service, fieldMappings.Select(f => f.Id).ToList());

                if (destEventType == DEST_EVENT_CREATE && !fieldMappings.Any())
                {
                    hasErrors = true;
                    errorLog.AppendLine("No field mappings for Create");
                    return;
                }

                // [PERF: TARGETED COLUMNSET] Build the set of source columns this run can
                // possibly need from everything resolved above, and retrieve only those -
                // instead of `new ColumnSet(true)` (every column of the source record, on
                // every Update, including on large tables like msdyn_projecttask/
                // msdyn_workorder). Falls back to the old full-record retrieve if the
                // targeted one fails for any reason (e.g. a stale/renamed column name),
                // so this is strictly additive - never less robust than before.
                Entity sourceEntity = targetEntity;
                if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    var neededColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var col in GetIntegrationConditionFields(service, mappingRecord.Id, errorLog))
                        if (!string.IsNullOrWhiteSpace(col)) neededColumns.Add(col.Trim());

                    foreach (var fm in fieldMappings)
                    {
                        var col = fm.GetAttributeValue<string>("mfd_sourcecolumn");
                        if (!string.IsNullOrWhiteSpace(col)) neededColumns.Add(col.Trim());
                    }

                    foreach (var condList in allMappingConditions.Values)
                        foreach (var cond in condList)
                        {
                            var c1 = cond.GetAttributeValue<string>("mfd_valuesourcecolumn");
                            var c2 = cond.GetAttributeValue<string>("mfd_destinationsourcecolumn");
                            if (!string.IsNullOrWhiteSpace(c1)) neededColumns.Add(c1.Trim());
                            if (!string.IsNullOrWhiteSpace(c2)) neededColumns.Add(c2.Trim());
                        }

                    if (destEventType == DEST_EVENT_UPDATE || destEventType == DEST_EVENT_DELETE)
                    {
                        foreach (var dc in GetDestinationConditions(service, mappingRecord.Id))
                        {
                            var col = dc.GetAttributeValue<string>("mfd_sourcecolumn");
                            if (!string.IsNullOrWhiteSpace(col)) neededColumns.Add(col.Trim());
                        }
                    }

                    // ValidateScheduler reads msdyn_scheduler off the source when copying
                    // msdyn_project -> msdyn_projecttask - always include it, it's cheap.
                    neededColumns.Add("msdyn_scheduler");

                    try
                    {
                        sourceEntity = neededColumns.Any()
                            ? service.Retrieve(sourceEntityName, targetEntity.Id, new ColumnSet(neededColumns.ToArray()))
                            : service.Retrieve(sourceEntityName, targetEntity.Id, new ColumnSet(false));
                        foreach (var attr in targetEntity.Attributes)
                            sourceEntity[attr.Key] = attr.Value;
                    }
                    catch (Exception ex)
                    {
                        errorLog.AppendLine($"Targeted Retrieve error, falling back to full record: {ex.Message}");
                        try
                        {
                            sourceEntity = service.Retrieve(sourceEntityName, targetEntity.Id, new ColumnSet(true));
                            foreach (var attr in targetEntity.Attributes)
                                sourceEntity[attr.Key] = attr.Value;
                        }
                        catch (Exception ex2)
                        {
                            errorLog.AppendLine($"Fallback retrieve also failed: {ex2.Message}");
                            hasErrors = true;
                            sourceEntity = targetEntity;
                        }
                    }
                }

                environmentUrl = GetEnvironmentUrl(service);
                if (!string.IsNullOrEmpty(environmentUrl) && sourceEntity.Id != Guid.Empty)
                    sourceRecordUrl = $"{environmentUrl}/main.aspx?etn={sourceEntityName}&id={sourceEntity.Id}&pagetype=entityrecord";

                if (!ValidateIntegrationConditions(service, mappingRecord.Id, sourceEntity, errorLog, preImage, metadataCache))
                {
                    wasSuccessful = true;
                    return;
                }

                switch (destEventType)
                {
                    case DEST_EVENT_CREATE:
                        var createResult = ExecuteDestinationCreate(service, errorLog, mappingRecord, sourceEntity, sourceEntityName, destinationEntityName, fieldMappings, allMappingConditions, metadataCache);
                        wasSuccessful = createResult.success;
                        hasErrors |= createResult.hasErrors;
                        if (createResult.createdId != Guid.Empty)
                        {
                            recordsAffected = 1;
                            affectedRecordIds.Add(createResult.createdId);
                        }
                        break;

                    case DEST_EVENT_UPDATE:
                        var updateResult = ExecuteDestinationUpdate(service, errorLog, mappingRecord, sourceEntity, destinationEntityName, fieldMappings, allMappingConditions, metadataCache);
                        wasSuccessful = updateResult.success;
                        recordsAffected = updateResult.recordsAffected;
                        hasErrors |= updateResult.hasErrors;
                        affectedRecordIds = updateResult.affectedIds;
                        break;

                    case DEST_EVENT_DELETE:
                        var deleteResult = ExecuteDestinationDelete(service, errorLog, mappingRecord, sourceEntity, destinationEntityName, metadataCache);
                        wasSuccessful = deleteResult.success;
                        recordsAffected = deleteResult.recordsAffected;
                        hasErrors |= deleteResult.hasErrors;
                        affectedRecordIds = deleteResult.affectedIds;
                        break;

                    default:
                        hasErrors = true;
                        errorLog.AppendLine($"Unknown event type: {destEventType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"Exception: {ex.Message}");
                hasErrors = true;
            }
            finally
            {
                if (isEnabled)
                {
                    SendNotifications(service, mappingRecord, errorLog, hasErrors, environmentUrl, recordsAffected, sourceRecordUrl, destinationEntityName, affectedRecordIds);
                    logRecordId = SaveLog(service, mappingRecord, errorLog, hasErrors, sourceRecordUrl, environmentUrl, destinationEntityName, recordsAffected, affectedRecordIds);
                }
            }
        }

        #region Change Detection

        /// [CHANGE DETECTION] Devuelve los logical names de las columnas trigger configuradas
        /// para el mapping, normalizadas a minusculas (igual que AutoRegisterPlugin al armar
        /// los filtering attributes).
        /// Fail-open: ante cualquier error devuelve lista vacia, lo que hace que no se filtre nada.
        private List<string> GetTriggerColumns(IOrganizationService service, Guid mappingId, StringBuilder errorLog)
        {
            try
            {
                var qe = new QueryExpression(TRIGGER_TABLE)
                {
                    ColumnSet = new ColumnSet("mfd_columnname"),
                    Criteria = new FilterExpression
                    {
                        Conditions = { new ConditionExpression("mfd_advancedintegration", ConditionOperator.Equal, mappingId) }
                    }
                };

                return service.RetrieveMultiple(qe).Entities
                    .Select(t => t.GetAttributeValue<string>("mfd_columnname"))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList();
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[TRIGGER FIELDS ERROR] {ex.Message}");
                return new List<string>();
            }
        }

        /// [CHANGE DETECTION] Compara el valor viejo (PreImage) contra el nuevo (Target).
        ///
        /// NO se reusa CompareValues() a proposito: ese metodo cae en Equals() para
        /// OptionSetValueCollection, que es igualdad por referencia y siempre daria "distinto",
        /// haciendo que los multiselect nunca se filtren.
        ///
        /// EntityReference compara tambien LogicalName porque en lookups polimorficos
        /// (customerid, regardingobjectid) cambiar de account a contact con el mismo Id
        /// es un cambio real.
        private bool HasValueChanged(object oldValue, object newValue)
        {
            if (oldValue is AliasedValue oav) oldValue = oav.Value;
            if (newValue is AliasedValue nav) newValue = nav.Value;

            if (oldValue == null && newValue == null) return false;

            if (oldValue == null || newValue == null) return true;

            if (oldValue is EntityReference oer && newValue is EntityReference ner)
                return oer.Id != ner.Id
                    || !string.Equals(oer.LogicalName, ner.LogicalName, StringComparison.OrdinalIgnoreCase);

            if (oldValue is OptionSetValue oos && newValue is OptionSetValue nos)
                return oos.Value != nos.Value;

            if (oldValue is OptionSetValueCollection ooc && newValue is OptionSetValueCollection noc)
                return !ooc.Select(o => o.Value).OrderBy(v => v)
                          .SequenceEqual(noc.Select(o => o.Value).OrderBy(v => v));

            if (oldValue is Money omn && newValue is Money nmn)
                return omn.Value != nmn.Value;

            if (oldValue is DateTime odt && newValue is DateTime ndt)
                return odt.ToUniversalTime() != ndt.ToUniversalTime();

            if (oldValue is decimal odc && newValue is decimal ndc)
                return odc != ndc;

            if (oldValue is double odb && newValue is double ndb)
                return odb != ndb;

            if (oldValue is string ostr && newValue is string nstr)
                return !string.Equals(ostr, nstr, StringComparison.Ordinal);

            return !Equals(oldValue, newValue);
        }

        /// [CHANGE DETECTION] Vuelca al log todo lo que identifica de donde vino esta llamada.
        /// Sirve para determinar por que se ejecuta dos veces la misma integracion.
        ///
        /// Lo mas util es CorrelationId: Dataverse lo comparte entre TODAS las operaciones que
        /// nacen de una misma request original. Comparando los dos logs del mismo registro:
        ///   - mismo CorrelationId      -> 1 llamada externa que cascadeo adentro de Dataverse.
        ///   - CorrelationId distinto   -> 2 requests externas independientes.
        ///
        /// Depth 1 = escritura directa desde afuera. Depth > 1 = disparada por algo interno,
        /// y en ese caso el "Parent chain" dice que la origino.
        ///
        /// SharedVariables del root: Dataverse suele dejar ahi cosas como CallerOrigin
        /// (Application / WebServiceApi / AsyncService...). No esta documentado oficialmente,
        /// asi que en vez de asumir la clave se vuelca el diccionario completo.
        private void AppendCallDiagnostics(IPluginExecutionContext context, Entity targetEntity, StringBuilder errorLog)
        {
            try
            {
                errorLog.AppendLine("--- CALL DIAGNOSTICS ---");
                errorLog.AppendLine($"CorrelationId  : {context.CorrelationId}");
                errorLog.AppendLine($"RequestId      : {context.RequestId}");
                errorLog.AppendLine($"OperationId    : {context.OperationId}");
                errorLog.AppendLine($"Depth          : {context.Depth}");
                errorLog.AppendLine($"Stage / Mode   : {context.Stage} / {(context.Mode == 0 ? "Sync" : "Async")}");
                errorLog.AppendLine($"InitiatingUser : {context.InitiatingUserId}");
                errorLog.AppendLine($"UserId         : {context.UserId}");
                errorLog.AppendLine($"Step           : {context.OwningExtension?.Name} ({context.OwningExtension?.Id})");

                var chain = new List<string>();
                var cur = context.ParentContext;
                int guard = 0;
                while (cur != null && guard++ < 10)
                {
                    chain.Add($"{cur.MessageName} on {cur.PrimaryEntityName} (depth {cur.Depth}, stage {cur.Stage})");
                    cur = cur.ParentContext;
                }
                errorLog.AppendLine(chain.Any()
                    ? $"Parent chain   : {string.Join("  <-  ", chain)}"
                    : "Parent chain   : (none - direct call from outside Dataverse)");

                var root = GetRootContext(context);
                if (root.SharedVariables != null && root.SharedVariables.Any())
                {
                    errorLog.AppendLine("Root SharedVariables:");
                    foreach (var kv in root.SharedVariables)
                    {
                        if (kv.Key != null && kv.Key.StartsWith("AdvInt_", StringComparison.OrdinalIgnoreCase)) continue;
                        errorLog.AppendLine($"  {kv.Key} = {FormatValueForLog(kv.Value)}");
                    }
                }

                errorLog.AppendLine($"Target payload ({targetEntity.Attributes.Count} attrs): {string.Join(", ", targetEntity.Attributes.Keys.OrderBy(k => k))}");
                errorLog.AppendLine("------------------------");
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[DIAGNOSTICS ERROR] {ex.Message}");
            }
        }

        #endregion

        #region Get Mapping Record

        private Entity GetMappingRecord(IOrganizationService service, IPluginExecutionContext context, StringBuilder errorLog)
        {
            try
            {
                if (!string.IsNullOrEmpty(_unsecureConfig) && Guid.TryParse(_unsecureConfig, out Guid mappingId))
                {
                    try { return service.Retrieve("mfd_advancedmapping", mappingId, new ColumnSet(true)); }
                    catch (Exception ex)
                    {
                        errorLog.AppendLine($"GetMappingRecord: config Guid {mappingId} not retrievable ({ex.Message}). Falling back to step query.");
                    }
                }

                Guid stepId = context.OwningExtension.Id;
                var qe = new QueryExpression("mfd_advancedmapping")
                {
                    ColumnSet = new ColumnSet(true),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("mfd_pluggingregistrationid", ConditionOperator.Equal, stepId.ToString()),
                            new ConditionExpression("mfd_applicationid", ConditionOperator.Equal, VALID_APPLICATION_ID)
                        }
                    }
                };

                var mapping = service.RetrieveMultiple(qe).Entities.FirstOrDefault();

                if (mapping != null)
                {
                    try
                    {
                        Entity updateStep = new Entity("sdkmessageprocessingstep", stepId);
                        updateStep["configuration"] = mapping.Id.ToString();
                        service.Update(updateStep);
                    }
                    catch { }
                }

                return mapping;
            }
            catch (Exception ex) { errorLog.AppendLine($"GetMappingRecord: {ex.Message}"); return null; }
        }

        #endregion

        #region Mapping Conditions

        private Dictionary<Guid, List<Entity>> GetAllMappingConditions(IOrganizationService service, List<Guid> fieldMappingIds)
        {
            var result = new Dictionary<Guid, List<Entity>>();
            if (!fieldMappingIds.Any()) return result;

            try
            {
                var qe = new QueryExpression("mfd_advancedintegrationmappingcondition")
                {

                    ColumnSet = new ColumnSet(
                        "mfd_advancedintegrationmapping",
                        "mfd_operator",
                        "mfd_value",
                        "mfd_destinationvalue",
                        "mfd_valuesourcecolumn",
                        "mfd_destinationsourcecolumn",
                        "mfd_comparedatatype",
                        "mfd_sequence"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("mfd_advancedintegrationmapping", ConditionOperator.In, fieldMappingIds.Cast<object>().ToArray())
                        }
                    }
                };

                var conditions = service.RetrieveMultiple(qe).Entities;

                foreach (var cond in conditions)
                {
                    var fieldMappingRef = cond.GetAttributeValue<EntityReference>("mfd_advancedintegrationmapping");
                    if (fieldMappingRef != null)
                    {
                        if (!result.ContainsKey(fieldMappingRef.Id))
                            result[fieldMappingRef.Id] = new List<Entity>();
                        result[fieldMappingRef.Id].Add(cond);
                    }
                }

                foreach (var key in result.Keys.ToList())
                {
                    result[key] = result[key]
                        .OrderBy(c => c.GetAttributeValue<int?>("mfd_sequence") ?? int.MaxValue)
                        .ToList();
                }
            }
            catch { }

            return result;
        }

        /// Recorre las condiciones (ya ordenadas por mfd_sequence) y devuelve el valor a escribir
        /// de la PRIMERA que matchea. Si ninguna matchea devuelve null, y el caller cae al default.
        ///
        /// [COLUMN-VS-COLUMN] Cada condicion puede ahora:
        ///  - comparar el source contra una CONSTANTE (mfd_value) o contra OTRA COLUMNA del
        ///    source (mfd_valuesourcecolumn). La comparacion es tipada segun mfd_comparedatatype.
        ///  - escribir una CONSTANTE (mfd_destinationvalue) o el CONTENIDO CRUDO de una columna
        ///    del source (mfd_destinationsourcecolumn), preservando EntityReference / OptionSet / etc.
        ///
        /// Todo es opt-in: si las columnas nuevas estan vacias, el comportamiento es identico
        /// al historico (constante vs constante).
        private object EvaluateAndGetDestinationValue(
     IOrganizationService service,
     string destEntityName,
     string destCol,
     object sourceValue,
     List<Entity> conditions,
     Entity source,
     StringBuilder errorLog,
     Dictionary<string, AttributeMetadata> metadataCache)
        {
            foreach (var cond in conditions)
            {
                try
                {
                    int operatorType = cond.GetAttributeValue<OptionSetValue>("mfd_operator")?.Value ?? -1;
                    int compareDataType = cond.GetAttributeValue<OptionSetValue>("mfd_comparedatatype")?.Value ?? DATATYPE_STRING;

                    string valueSourceColumn = cond.GetAttributeValue<string>("mfd_valuesourcecolumn");
                    string destinationSourceColumn = cond.GetAttributeValue<string>("mfd_destinationsourcecolumn");
                    string conditionConstant = cond.GetAttributeValue<string>("mfd_value");
                    string destinationConstant = cond.GetAttributeValue<string>("mfd_destinationvalue");
                    bool rightIsColumn = !string.IsNullOrWhiteSpace(valueSourceColumn);
                    object compareRight;
                    if (rightIsColumn)
                    {
                        string col = valueSourceColumn.Trim();
                        if (!source.Contains(col) || source[col] == null)
                        {
                            errorLog.AppendLine($"[MAPPING COND] value column '{col}' missing/null -> condition skipped");
                            continue;
                        }
                        compareRight = source[col];
                    }
                    else
                    {
                        compareRight = conditionConstant;
                    }

                    bool matched = rightIsColumn
                        ? EvaluateTypedCondition(sourceValue, compareRight, operatorType, compareDataType)
                        : EvaluateMappingCondition(sourceValue, operatorType, conditionConstant);

                    if (!matched)
                        continue;

                    // La condicion matcheo -> resolvemos el valor a escribir.
                    // Opcion dinamica: contenido CRUDO de una columna del source (preserva el tipo).
                    if (!string.IsNullOrWhiteSpace(destinationSourceColumn))
                    {
                        string dcol = destinationSourceColumn.Trim();
                        if (!source.Contains(dcol) || source[dcol] == null)
                        {
                            // La condicion matcheo pero el valor a copiar no existe.
                            // No inventamos nada: caemos al default del caller devolviendo null.
                            errorLog.AppendLine($"[MAPPING COND] matched but destination column '{dcol}' missing/null -> falling back to default");
                            return null;
                        }

                        object raw = source[dcol];
                        if (raw is AliasedValue av) raw = av.Value;
                        // ConvertToSafeType adapta el valor crudo (EntityReference, OptionSet, etc.)
                        // al tipo real de la columna destino.
                        return ConvertToSafeType(service, destEntityName, destCol, raw, errorLog, metadataCache);
                    }

                    // Opcion historica: constante.
                    return ConvertToSafeType(service, destEntityName, destCol, destinationConstant, errorLog, metadataCache);
                }
                catch (Exception ex)
                {
                    errorLog.AppendLine($"[MAPPING COND ERROR] {ex.Message}");
                }
            }

            return null;
        }

        /// [COLUMN-VS-COLUMN] Compara dos valores CRUDOS (no strings) de forma tipada, segun el
        /// datatype configurado. Se usa cuando el lado derecho de la condicion es otra columna
        /// del source, para que "10 > 9" sea numerico y no lexicografico, y para que fechas /
        /// decimales / etc. comparen bien.
        ///
        /// Reutiliza el mismo criterio de tipado que ExtractQueryValue / TryCompare del resto
        /// del plugin, manteniendo la logica generica para cualquier tipo.
        private bool EvaluateTypedCondition(object leftValue, object rightValue, int operatorType, int dataType)
        {
            if (operatorType == OPERATOR_IS_NULL)
                return leftValue == null;
            if (operatorType == OPERATOR_IS_NOT_NULL)
                return leftValue != null;

            object left = CoerceForCompare(leftValue, dataType);
            object right = CoerceForCompare(rightValue, dataType);

            switch (operatorType)
            {
                case OPERATOR_EQUAL:
                    return TypedEquals(left, right, dataType);

                case OPERATOR_NOT_EQUAL:
                    return !TypedEquals(left, right, dataType);

                case OPERATOR_CONTAINS:
                    string ls = left?.ToString() ?? "";
                    string rs = right?.ToString() ?? "";
                    return ls.IndexOf(rs, StringComparison.OrdinalIgnoreCase) >= 0;

                case OPERATOR_GREATER_THAN:
                    return TypedCompare(left, right, dataType, out int gc) && gc > 0;

                case OPERATOR_LESS_THAN:
                    return TypedCompare(left, right, dataType, out int lc) && lc < 0;

                default:
                    return false;
            }
        }

        /// [COLUMN-VS-COLUMN] Convierte un valor crudo de Dataverse al tipo comparable pedido.
        /// Desenvuelve OptionSetValue -> int, Money -> decimal, EntityReference -> Guid.
        private object CoerceForCompare(object value, int dataType)
        {
            if (value == null) return null;
            if (value is AliasedValue av) value = av.Value;

            if (value is OptionSetValue osv) value = osv.Value;
            else if (value is Money m) value = m.Value;
            else if (value is EntityReference er) value = er.Id;

            try
            {
                switch (dataType)
                {
                    case DATATYPE_INTEGER: return Convert.ToInt64(value);
                    case DATATYPE_DECIMAL: return Convert.ToDecimal(value);
                    case DATATYPE_BOOLEAN: return Convert.ToBoolean(value);
                    case DATATYPE_DATETIME: return Convert.ToDateTime(value);
                    case DATATYPE_GUID:
                        if (value is Guid) return value;
                        return Guid.TryParse(value.ToString(), out Guid g) ? g : (object)value.ToString();
                    default: return value.ToString();
                }
            }
            catch
            {
                return value.ToString();
            }
        }

        private bool TypedEquals(object left, object right, int dataType)
        {
            if (left == null && right == null) return true;
            if (left == null || right == null) return false;

            if (dataType == DATATYPE_STRING)
                return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);

            return left.Equals(right);
        }

        private bool TypedCompare(object left, object right, int dataType, out int result)
        {
            result = 0;
            if (left == null || right == null) return false;

            try
            {
                switch (dataType)
                {
                    case DATATYPE_INTEGER:
                        result = Convert.ToInt64(left).CompareTo(Convert.ToInt64(right));
                        return true;
                    case DATATYPE_DECIMAL:
                        result = Convert.ToDecimal(left).CompareTo(Convert.ToDecimal(right));
                        return true;
                    case DATATYPE_DATETIME:
                        result = Convert.ToDateTime(left).CompareTo(Convert.ToDateTime(right));
                        return true;
                    case DATATYPE_BOOLEAN:
                        result = Convert.ToBoolean(left).CompareTo(Convert.ToBoolean(right));
                        return true;
                    default:
                        if (decimal.TryParse(left.ToString(), out decimal dl) && decimal.TryParse(right.ToString(), out decimal dr))
                        {
                            result = dl.CompareTo(dr);
                            return true;
                        }
                        result = string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
                        return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool EvaluateMappingCondition(object sourceValue, int operatorType, string conditionValue)
        {
            string sourceValueStr = GetStringValue(sourceValue);

            switch (operatorType)
            {
                case OPERATOR_EQUAL:
                    return string.Equals(sourceValueStr, conditionValue, StringComparison.OrdinalIgnoreCase);
                case OPERATOR_NOT_EQUAL:
                    return !string.Equals(sourceValueStr, conditionValue, StringComparison.OrdinalIgnoreCase);
                case OPERATOR_CONTAINS:
                    return sourceValueStr != null && sourceValueStr.IndexOf(conditionValue ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
                case OPERATOR_GREATER_THAN:
                    return CompareNumeric(sourceValueStr, conditionValue) > 0;
                case OPERATOR_LESS_THAN:
                    return CompareNumeric(sourceValueStr, conditionValue) < 0;
                case OPERATOR_IS_NULL:
                    return sourceValue == null || string.IsNullOrEmpty(sourceValueStr);
                case OPERATOR_IS_NOT_NULL:
                    return sourceValue != null && !string.IsNullOrEmpty(sourceValueStr);
                default:
                    return false;
            }
        }

        private string GetStringValue(object value)
        {
            if (value == null) return null;
            if (value is OptionSetValue osv) return osv.Value.ToString();
            if (value is Money m) return m.Value.ToString();
            if (value is EntityReference er) return er.Id.ToString();
            if (value is bool b) return b ? "1" : "0";
            if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss");
            return value.ToString();
        }

        private int CompareNumeric(string value1, string value2)
        {
            if (decimal.TryParse(value1, out decimal dec1) && decimal.TryParse(value2, out decimal dec2))
                return dec1.CompareTo(dec2);
            return string.Compare(value1 ?? "", value2 ?? "", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Related Entity Lookup

        /// Obtiene un valor de una entidad relacionada a través de un campo lookup
        /// Ejemplo: Source tiene teamid (lookup a team), queremos obtener businessunitid del team
        private object GetRelatedEntityValue(IOrganizationService service, Entity source, string lookupField, string relatedField, StringBuilder errorLog)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lookupField) || string.IsNullOrWhiteSpace(relatedField))
                    return null;

                if (!source.Contains(lookupField))
                    return null;

                var lookupRef = source[lookupField] as EntityReference;
                if (lookupRef == null)
                    return null;

                var relatedEntity = service.Retrieve(lookupRef.LogicalName, lookupRef.Id, new ColumnSet(relatedField));

                if (relatedEntity.Contains(relatedField))
                    return relatedEntity[relatedField];

                return null;
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[RELATED ENTITY ERROR] {lookupField}.{relatedField}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Destination Operations

        private (bool success, Guid createdId, bool hasErrors) ExecuteDestinationCreate(
            IOrganizationService service, StringBuilder errorLog, Entity mapping, Entity source,
            string sourceEntityName, string destEntityName, List<Entity> fieldMappings,
            Dictionary<Guid, List<Entity>> allMappingConditions, Dictionary<string, AttributeMetadata> metadataCache)
        {
            Entity newEntity = new Entity(destEntityName);
            bool hasErrors = false;

            try
            {
                foreach (var fm in fieldMappings)
                {
                    string destCol = null;
                    string srcCol = null;
                    object val = null;

                    try
                    {
                        destCol = fm.GetAttributeValue<string>("mfd_destinationcolumn");
                        srcCol = fm.GetAttributeValue<string>("mfd_sourcecolumn");
                        bool useDef = fm.GetAttributeValue<bool?>("mfd_usedefaultvalue") ?? false;
                        string defVal = fm.GetAttributeValue<string>("mfd_defaultvalueifnonefound");
                        bool useCondition = fm.GetAttributeValue<bool?>("mfd_usecondition") ?? false;

                        if (string.IsNullOrWhiteSpace(destCol)) continue;
                        if (string.IsNullOrWhiteSpace(srcCol) && !useDef && !useCondition) continue;

                        object sourceValue = null;

                        //// Primero: verificar si debe obtener valor de entidad relacionada
                        //if (useRelated && !string.IsNullOrWhiteSpace(lookupField) && !string.IsNullOrWhiteSpace(relatedField))
                        //{
                        //    sourceValue = GetRelatedEntityValue(service, source, lookupField, relatedField, errorLog);
                        //}
                        //// Segundo: obtener valor directo del source
                        //else
                        if (!string.IsNullOrWhiteSpace(srcCol) && source.Contains(srcCol))
                        {
                            sourceValue = source[srcCol];
                        }

                        if (useCondition)
                        {
                            var conditions = allMappingConditions.ContainsKey(fm.Id) ? allMappingConditions[fm.Id] : new List<Entity>();

                            if (conditions.Any())
                            {
                                val = EvaluateAndGetDestinationValue(service, destEntityName, destCol, sourceValue, conditions, source, errorLog, metadataCache);

                                if (val == null && useDef && !string.IsNullOrWhiteSpace(defVal))
                                    val = defVal;
                            }
                            else if (useDef && !string.IsNullOrWhiteSpace(defVal))
                            {
                                val = defVal;
                            }
                        }
                        else
                        {
                            if (!useDef && sourceValue != null)
                                val = sourceValue;
                            if (val == null && useDef)
                                val = defVal;
                        }

                        if (val != null && !string.IsNullOrWhiteSpace(destCol))
                        {
                            var convertedVal = ConvertToSafeType(service, destEntityName, destCol, val, errorLog, metadataCache);
                            newEntity[destCol] = convertedVal;
                        }
                    }
                    catch (Exception ex)
                    {
                        hasErrors = true;
                        errorLog.AppendLine($"[FIELD ERROR] {srcCol ?? "default"} → {destCol}: {ex.Message}");
                    }
                }

                if (!ValidateScheduler(errorLog, source, sourceEntityName, destEntityName, newEntity))
                    return (false, Guid.Empty, true);

                try
                {
                    Guid createdId = service.Create(newEntity);
                    return (true, createdId, hasErrors);
                }
                catch (Exception createEx)
                {
                    hasErrors = true;
                    errorLog.AppendLine($"[CREATE ERROR] {createEx.Message}");

                    var fieldFromEntityRef = FindFieldByEntityReferenceInError(createEx.Message, newEntity);
                    if (fieldFromEntityRef != null)
                    {
                        errorLog.AppendLine($"  [FIELD FROM MAPPING] {fieldFromEntityRef.Value.Key} = {FormatValueForLog(fieldFromEntityRef.Value.Value)}");
                        errorLog.AppendLine($"  The referenced record does not exist. Check if the source value is correct.");
                        return (false, Guid.Empty, true);
                    }

                    string fieldFromError = ExtractFieldFromError(createEx.Message);
                    if (!string.IsNullOrEmpty(fieldFromError) && newEntity.Contains(fieldFromError))
                    {
                        errorLog.AppendLine($"  [FIELD FROM MAPPING] {fieldFromError} = {FormatValueForLog(newEntity[fieldFromError])}");
                        return (false, Guid.Empty, true);
                    }

                    var suspectFields = FindFieldsByTypeInError(createEx.Message, newEntity);
                    if (suspectFields.Any())
                    {
                        errorLog.AppendLine($"  Possible fields from mapping with this type:");
                        foreach (var sf in suspectFields.Take(10))
                            errorLog.AppendLine($"    - {sf.Key} = {FormatValueForLog(sf.Value)}");
                        return (false, Guid.Empty, true);
                    }

                    var allLookups = newEntity.Attributes.Where(a => a.Value is EntityReference).ToList();
                    if (allLookups.Any())
                    {
                        errorLog.AppendLine($"  [!] Referenced record not found directly in mapping.");
                        errorLog.AppendLine($"  Lookups in this mapping ({allLookups.Count}):");
                        foreach (var lk in allLookups)
                        {
                            var er = (EntityReference)lk.Value;
                            errorLog.AppendLine($"    - {lk.Key} → {er.LogicalName} ({er.Id})");
                        }
                    }

                    return (false, Guid.Empty, true);
                }
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[UNEXPECTED ERROR] {ex.Message}");
                return (false, Guid.Empty, true);
            }
        }

        private (bool success, int recordsAffected, bool hasErrors, List<Guid> affectedIds) ExecuteDestinationUpdate(
            IOrganizationService service, StringBuilder errorLog, Entity mapping, Entity source,
            string destEntityName, List<Entity> fieldMappings, Dictionary<Guid, List<Entity>> allMappingConditions,
            Dictionary<string, AttributeMetadata> metadataCache)
        {
            List<Guid> affectedIds = new List<Guid>();
            bool hasErrors = false;

            try
            {
                var conditions = GetDestinationConditions(service, mapping.Id);
                if (!conditions.Any()) { errorLog.AppendLine("No destination conditions for Update"); return (false, 0, true, affectedIds); }

                var targetRecords = GetDestinationRecords(service, errorLog, destEntityName, conditions, source, metadataCache);
                if (targetRecords == null) return (false, 0, true, affectedIds);
                if (!targetRecords.Any()) { errorLog.AppendLine("No records match conditions"); return (true, 0, false, affectedIds); }

                if (targetRecords.Count > MAX_TOTAL_RECORDS)
                {
                    errorLog.AppendLine($"WARNING: Limiting to {MAX_TOTAL_RECORDS} records");
                    targetRecords = targetRecords.Take(MAX_TOTAL_RECORDS).ToList();
                }

                affectedIds = targetRecords.Select(r => r.Id).ToList();

                var updateValues = new Dictionary<string, object>();
                foreach (var fm in fieldMappings)
                {
                    string destCol = null;
                    string srcCol = null;
                    object val = null;

                    try
                    {
                        destCol = fm.GetAttributeValue<string>("mfd_destinationcolumn");
                        srcCol = fm.GetAttributeValue<string>("mfd_sourcecolumn");
                        bool useDef = fm.GetAttributeValue<bool?>("mfd_usedefaultvalue") ?? false;
                        string defVal = fm.GetAttributeValue<string>("mfd_defaultvalueifnonefound");
                        bool useCondition = fm.GetAttributeValue<bool?>("mfd_usecondition") ?? false;

                        //bool useRelated = fm.GetAttributeValue<bool?>("mfd_userelatedentity") ?? false;
                        //string lookupField = fm.GetAttributeValue<string>("mfd_lookupfield");
                        //string relatedField = fm.GetAttributeValue<string>("mfd_relatedentityfield");

                        object sourceValue = null;

                        //if (useRelated && !string.IsNullOrWhiteSpace(lookupField) && !string.IsNullOrWhiteSpace(relatedField))
                        //{
                        //    sourceValue = GetRelatedEntityValue(service, source, lookupField, relatedField, errorLog);
                        //}
                        //else 
                        if (!string.IsNullOrWhiteSpace(srcCol) && source.Contains(srcCol))
                        {
                            sourceValue = source[srcCol];
                        }

                        if (useCondition)
                        {
                            var mappingConditions = allMappingConditions.ContainsKey(fm.Id) ? allMappingConditions[fm.Id] : new List<Entity>();

                            if (mappingConditions.Any())
                            {
                                val = EvaluateAndGetDestinationValue(service, destEntityName, destCol, sourceValue, mappingConditions, source, errorLog, metadataCache);
                                if (val == null && useDef && !string.IsNullOrWhiteSpace(defVal))
                                    val = defVal;
                            }
                            else if (useDef && !string.IsNullOrWhiteSpace(defVal))
                            {
                                val = defVal;
                            }
                        }
                        else
                        {
                            if (!useDef && sourceValue != null)
                                val = sourceValue;
                            if (val == null && useDef)
                                val = defVal;
                        }

                        if (val != null && !string.IsNullOrWhiteSpace(destCol))
                            updateValues[destCol] = ConvertToSafeType(service, destEntityName, destCol, val, errorLog, metadataCache);
                    }
                    catch (Exception ex)
                    {
                        hasErrors = true;
                        errorLog.AppendLine($"[FIELD ERROR] {srcCol ?? "default"} → {destCol}: {ex.Message}");
                    }
                }

                if (!updateValues.Any()) { errorLog.AppendLine("No values to update"); return (true, 0, false, affectedIds); }

                int count = ExecuteBatchUpdate(service, errorLog, destEntityName, targetRecords, updateValues, ref hasErrors);
                return (true, count, hasErrors, affectedIds);
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[UPDATE ERROR] {ex.Message}");
                return (false, 0, true, affectedIds);
            }
        }

        private (bool success, int recordsAffected, bool hasErrors, List<Guid> affectedIds) ExecuteDestinationDelete(
            IOrganizationService service, StringBuilder errorLog, Entity mapping, Entity source, string destEntityName,
            Dictionary<string, AttributeMetadata> metadataCache)
        {
            List<Guid> affectedIds = new List<Guid>();
            bool hasErrors = false;

            try
            {
                var conditions = GetDestinationConditions(service, mapping.Id);
                if (!conditions.Any()) { errorLog.AppendLine("No destination conditions for Delete"); return (false, 0, true, affectedIds); }

                var targetRecords = GetDestinationRecords(service, errorLog, destEntityName, conditions, source, metadataCache);
                if (targetRecords == null) return (false, 0, true, affectedIds);
                if (!targetRecords.Any()) { errorLog.AppendLine("No records match conditions"); return (true, 0, false, affectedIds); }

                if (targetRecords.Count > MAX_TOTAL_RECORDS)
                {
                    errorLog.AppendLine($"WARNING: Limiting to {MAX_TOTAL_RECORDS} records");
                    targetRecords = targetRecords.Take(MAX_TOTAL_RECORDS).ToList();
                }

                affectedIds = targetRecords.Select(r => r.Id).ToList();

                int count = ExecuteBatchDelete(service, errorLog, destEntityName, targetRecords, ref hasErrors);
                return (true, count, hasErrors, affectedIds);
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[DELETE ERROR] {ex.Message}");
                return (false, 0, true, affectedIds);
            }
        }

        #endregion

        #region Destination Conditions

        private List<Entity> GetDestinationConditions(IOrganizationService service, Guid mappingId)
        {
            try
            {
                var qe = new QueryExpression("mfd_advancedintegrationdestinationcondition")
                {
                    ColumnSet = new ColumnSet("mfd_destinationcolumn", "mfd_usedefaultvalue", "mfd_defaultvalue", "mfd_sourcecolumn", "mfd_operator", "mfd_datatype"),
                    Criteria = new FilterExpression { Conditions = { new ConditionExpression("mfd_advancedintegration", ConditionOperator.Equal, mappingId) } }
                };
                return service.RetrieveMultiple(qe).Entities.ToList();
            }
            catch { return new List<Entity>(); }
        }

        private List<Entity> GetDestinationRecords(IOrganizationService service, StringBuilder errorLog, string destEntityName, List<Entity> conditions, Entity source, Dictionary<string, AttributeMetadata> metadataCache)
        {
            try
            {
                // DateTime NOT_EQUAL conditions are evaluated in C# (date-only comparison) to avoid
                // Dataverse treating a time-component difference as a real change and causing a loop.
                // We collect them separately and fetch the relevant columns so we can filter after the query.
                var dateNotEqualChecks = new List<(string destCol, DateTime compareDate)>();
                var dateColumnsToFetch = new List<string>();

                var qe = new QueryExpression(destEntityName)
                {
                    ColumnSet = new ColumnSet(false),
                    Criteria = new FilterExpression(LogicalOperator.And)
                };

                int conditionsAdded = 0;

                int queryConditionsAdded = 0;

                foreach (var cond in conditions)
                {
                    string destCol = cond.GetAttributeValue<string>("mfd_destinationcolumn");
                    bool useDef = cond.GetAttributeValue<bool?>("mfd_usedefaultvalue") ?? false;
                    string defVal = cond.GetAttributeValue<string>("mfd_defaultvalue");
                    string srcCol = cond.GetAttributeValue<string>("mfd_sourcecolumn");
                    int opValue = cond.GetAttributeValue<OptionSetValue>("mfd_operator")?.Value ?? OPERATOR_EQUAL;
                    int dataType = cond.GetAttributeValue<OptionSetValue>("mfd_datatype")?.Value ?? DATATYPE_STRING;

                    if (string.IsNullOrWhiteSpace(destCol)) continue;

                    object compareValue = null;
                    if (useDef) compareValue = ParseValueByDataType(defVal, dataType);
                    else if (!string.IsNullOrWhiteSpace(srcCol) && source.Contains(srcCol))
                    {
                        compareValue = source[srcCol];
                        compareValue = ConvertToSafeType(service, destEntityName, destCol, compareValue, errorLog, metadataCache);
                    }

                    if (opValue == OPERATOR_NOT_EQUAL && compareValue is DateTime dtCompare)
                    {
                        dateNotEqualChecks.Add((destCol, dtCompare.Date));
                        dateColumnsToFetch.Add(destCol);
                        conditionsAdded++;
                        continue;
                    }

                    if (opValue == OPERATOR_IS_NULL) { qe.Criteria.AddCondition(destCol, ConditionOperator.Null); conditionsAdded++; queryConditionsAdded++; }
                    else if (opValue == OPERATOR_IS_NOT_NULL) { qe.Criteria.AddCondition(destCol, ConditionOperator.NotNull); conditionsAdded++; queryConditionsAdded++; }
                    else if (compareValue != null) { qe.Criteria.AddCondition(destCol, GetConditionOperator(opValue), ExtractQueryValue(compareValue, dataType)); conditionsAdded++; queryConditionsAdded++; }
                }

                if (conditionsAdded == 0)
                {
                    errorLog.AppendLine("ERROR: No valid conditions - aborted for safety");
                    return null;
                }

                if (queryConditionsAdded == 0)
                {
                    errorLog.AppendLine("ERROR: The only destination conditions are DateTime NOT_EQUAL (evaluated in C#). " +
                                        "The query would run unfiltered over the whole table - aborted for safety.");
                    return null;
                }

                if (dateColumnsToFetch.Any())
                    qe.ColumnSet = new ColumnSet(dateColumnsToFetch.ToArray());

                qe.TopCount = MAX_TOTAL_RECORDS + 1;
                var records = service.RetrieveMultiple(qe).Entities.ToList();

                if (dateNotEqualChecks.Any())
                {
                    records = records.Where(rec =>
                    {
                        foreach (var (destCol, compareDate) in dateNotEqualChecks)
                        {
                            if (rec.Contains(destCol) && rec[destCol] is DateTime existingDate)
                            {
                                if (existingDate.Date == compareDate)
                                    return false;
                            }
                        }
                        return true;
                    }).ToList();
                }

                return records;
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[QUERY ERROR] {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Batch Operations

        private int ExecuteBatchUpdate(IOrganizationService service, StringBuilder errorLog, string entityName, List<Entity> records, Dictionary<string, object> values, ref bool hasErrors)
        {
            int successCount = 0, errorCount = 0;

            for (int i = 0; i < records.Count; i += MAX_RECORDS_PER_BATCH)
            {
                var batch = records.Skip(i).Take(MAX_RECORDS_PER_BATCH).ToList();
                var request = new ExecuteMultipleRequest
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }
                };

                foreach (var rec in batch)
                {
                    var update = new Entity(entityName, rec.Id);
                    foreach (var kvp in values) update[kvp.Key] = kvp.Value;
                    request.Requests.Add(new UpdateRequest { Target = update });
                }

                try
                {
                    var response = (ExecuteMultipleResponse)service.Execute(request);

                    foreach (var item in response.Responses)
                    {
                        if (item.Fault != null)
                        {
                            errorCount++;
                            if (errorCount <= 5)
                            {
                                var failedRecordId = (item.RequestIndex >= 0 && item.RequestIndex < batch.Count)
                                    ? batch[item.RequestIndex].Id
                                    : Guid.Empty;
                                errorLog.AppendLine($"[UPDATE ERROR] {failedRecordId}: {item.Fault.Message}");
                            }
                        }
                        else successCount++;
                    }
                }
                catch (Exception ex)
                {
                    errorLog.AppendLine($"[BATCH ERROR] {ex.Message}");
                    hasErrors = true;
                }
            }

            if (errorCount > 0)
            {
                hasErrors = true;
                if (errorCount > 5) errorLog.AppendLine($"... and {errorCount - 5} more errors");
                errorLog.AppendLine($"Total update errors: {errorCount}");
            }
            return successCount;
        }

        private int ExecuteBatchDelete(IOrganizationService service, StringBuilder errorLog, string entityName, List<Entity> records, ref bool hasErrors)
        {
            int successCount = 0, errorCount = 0;

            for (int i = 0; i < records.Count; i += MAX_RECORDS_PER_BATCH)
            {
                var batch = records.Skip(i).Take(MAX_RECORDS_PER_BATCH).ToList();
                var request = new ExecuteMultipleRequest
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }
                };

                foreach (var rec in batch)
                    request.Requests.Add(new DeleteRequest { Target = new EntityReference(entityName, rec.Id) });

                try
                {
                    var response = (ExecuteMultipleResponse)service.Execute(request);

                    foreach (var item in response.Responses)
                    {
                        if (item.Fault != null)
                        {
                            errorCount++;
                            if (errorCount <= 5)
                            {
                                var failedRecordId = (item.RequestIndex >= 0 && item.RequestIndex < batch.Count)
                                    ? batch[item.RequestIndex].Id
                                    : Guid.Empty;
                                errorLog.AppendLine($"[DELETE ERROR] {failedRecordId}: {item.Fault.Message}");
                            }
                        }
                        else successCount++;
                    }
                }
                catch (Exception ex)
                {
                    errorLog.AppendLine($"[BATCH ERROR] {ex.Message}");
                    hasErrors = true;
                }
            }

            if (errorCount > 0)
            {
                hasErrors = true;
                if (errorCount > 5) errorLog.AppendLine($"... and {errorCount - 5} more errors");
                errorLog.AppendLine($"Total delete errors: {errorCount}");
            }
            return successCount;
        }

        #endregion

        #region Logging

        private Guid? SaveLog(IOrganizationService service, Entity mapping, StringBuilder errorLog, bool hasErrors,
            string sourceUrl, string envUrl, string destEntity, int recordsAffected, List<Guid> affectedIds)
        {
            try
            {
                if (mapping == null) return null;

                bool logUsage = mapping.GetAttributeValue<bool?>("mfd_logusage") ?? false;
                if (!logUsage) return null;

                if (errorLog.Length == 0) errorLog.AppendLine("Executed successfully");
                if (recordsAffected > 0) errorLog.AppendLine($"\n=== RECORDS AFFECTED: {recordsAffected} ===");

                if (affectedIds.Any() && !string.IsNullOrEmpty(envUrl) && !string.IsNullOrEmpty(destEntity))
                {
                    errorLog.AppendLine("\n=== DESTINATION URLs ===");
                    foreach (var id in affectedIds.Take(MAX_URLS_IN_LOG))
                        errorLog.AppendLine($"{envUrl}/main.aspx?etn={destEntity}&id={id}&pagetype=entityrecord");
                    if (affectedIds.Count > MAX_URLS_IN_LOG)
                        errorLog.AppendLine($"... and {affectedIds.Count - MAX_URLS_IN_LOG} more");
                }

                var log = new Entity("mfd_advancedintegrationlog");
                log["mfd_advancedintegration"] = new EntityReference("mfd_advancedmapping", mapping.Id);
                log["mfd_description"] = TruncateIfNeeded(errorLog.ToString(), 3900);
                log["mfd_successfullycompleted"] = !hasErrors;
                log["mfd_errorsencountered"] = hasErrors;

                if (!string.IsNullOrEmpty(sourceUrl)) log["mfd_sourcerecordurl"] = sourceUrl;

                if (affectedIds.Any() && !string.IsNullOrEmpty(envUrl) && !string.IsNullOrEmpty(destEntity))
                    log["mfd_recordurl"] = $"{envUrl}/main.aspx?etn={destEntity}&id={affectedIds.First()}&pagetype=entityrecord";

                return service.Create(log);
            }
            catch (Exception ex)
            {
                try
                {
                    if (mapping != null)
                    {
                        var fallback = new Entity("mfd_advancedintegrationlog");
                        fallback["mfd_advancedintegration"] = new EntityReference("mfd_advancedmapping", mapping.Id);
                        fallback["mfd_description"] = $"Log save error: {ex.Message}";
                        fallback["mfd_errorsencountered"] = true;
                        return service.Create(fallback);
                    }
                }
                catch { }
                return null;
            }
        }

        #endregion

        #region Notifications

        private void SendNotifications(IOrganizationService service, Entity mapping, StringBuilder errorLog,
            bool hasErrors, string envUrl, int recordsAffected, string sourceUrl, string destEntity, List<Guid> affectedIds)
        {
            if (mapping == null) return;

            try
            {
                bool sendNotifications = mapping.GetAttributeValue<bool?>("mfd_sendnotificaitononerror") ?? false;
                if (!sendNotifications) return;

                string mappingName = mapping.GetAttributeValue<string>("mfd_eventfortable") ?? "Unknown Integration";
                string title = hasErrors
                    ? $"Integration Error: {mappingName}"
                    : $"Integration Success: {mappingName}";

                string affectedUrl = (affectedIds != null && affectedIds.Any() && !string.IsNullOrEmpty(envUrl) && !string.IsNullOrEmpty(destEntity))
                    ? $"{envUrl}/main.aspx?etn={destEntity}&id={affectedIds.First()}&pagetype=entityrecord"
                    : "N/A";

                string body = hasErrors
                    ? $"The integration encountered errors. Records affected: {recordsAffected}\n" +
                      $"Source Record URL: {sourceUrl}\n" +
                      $"Affected Record URL: {affectedUrl}"
                    : $"The integration completed successfully. Records affected: {recordsAffected}\n" +
                      $"Source Record URL: {sourceUrl}";

                int iconType = hasErrors ? ICON_FAILURE : ICON_SUCCESS;

                var ownerRef = mapping.GetAttributeValue<EntityReference>("ownerid");
                var teamRef = mapping.GetAttributeValue<EntityReference>("mfd_errornotificationteam");

                List<int> teamMethodsList = new List<int>();
                try
                {
                    var teamMethods = mapping.GetAttributeValue<OptionSetValueCollection>("mfd_notificationteamonerrorvia");
                    if (teamMethods != null)
                        teamMethodsList = teamMethods.Select(m => m.Value).ToList();
                }
                catch { }

                if (teamRef != null && teamMethodsList.Any())
                {
                    var teamUserIds = GetTeamMembers(service, teamRef.Id);
                    foreach (var method in teamMethodsList)
                        SendNotificationByMethod(service, method, teamUserIds, title, body, iconType, ownerRef, errorLog);
                }

                bool notifyUser = mapping.GetAttributeValue<bool?>("mfd_notifyuser") ?? false;

                List<int> userMethodsList = new List<int>();
                try
                {
                    var userMethods = mapping.GetAttributeValue<OptionSetValueCollection>("mfd_usernotificationmehtod");
                    if (userMethods != null)
                        userMethodsList = userMethods.Select(m => m.Value).ToList();
                }
                catch { }

                if (notifyUser && ownerRef != null && userMethodsList.Any())
                {
                    var userIds = new List<Guid>();
                    if (ownerRef.LogicalName == "systemuser")
                        userIds.Add(ownerRef.Id);
                    else if (ownerRef.LogicalName == "team")
                        userIds = GetTeamMembers(service, ownerRef.Id);

                    foreach (var method in userMethodsList)
                        SendNotificationByMethod(service, method, userIds, title, body, iconType, ownerRef, errorLog);
                }
            }
            catch { }
        }

        private void SendNotificationByMethod(IOrganizationService service, int method, List<Guid> userIds,
            string title, string body, int iconType, EntityReference senderRef, StringBuilder errorLog)
        {
            if (!userIds.Any()) return;

            switch (method)
            {
                case NOTIFY_IN_APP:
                    SendInAppNotifications(service, userIds, title, body, iconType, errorLog);
                    break;
                case NOTIFY_EMAIL:
                    SendEmailNotifications(service, userIds, title, body, senderRef, errorLog);
                    break;
            }
        }

        private void SendInAppNotifications(IOrganizationService service, List<Guid> userIds,
            string title, string body, int iconType, StringBuilder errorLog)
        {
            foreach (var userId in userIds)
            {
                try
                {
                    var notif = new Entity("appnotification");
                    notif["title"] = title;
                    notif["body"] = TruncateIfNeeded(body, 450);
                    notif["ownerid"] = new EntityReference("systemuser", userId);
                    notif["icontype"] = new OptionSetValue(iconType);
                    notif["toasttype"] = new OptionSetValue(TOAST_TIMED);
                    service.Create(notif);
                }
                catch { }
            }
        }

        private void SendEmailNotifications(IOrganizationService service, List<Guid> userIds,
            string title, string body, EntityReference senderRef, StringBuilder errorLog)
        {
            try
            {
                if (!userIds.Any()) return;

                var fromParty = new Entity("activityparty");
                fromParty["partyid"] = senderRef;

                var recipients = userIds.Select(id =>
                {
                    var party = new Entity("activityparty");
                    party["partyid"] = new EntityReference("systemuser", id);
                    return party;
                }).ToArray();

                var email = new Entity("email");
                email["subject"] = title;
                email["description"] = body;
                email["from"] = new Entity[] { fromParty };
                email["to"] = recipients;
                email["directioncode"] = true;

                Guid emailId = service.Create(email);

                var sendReq = new OrganizationRequest("SendEmail");
                sendReq["EmailId"] = emailId;
                sendReq["IssueSend"] = true;
                sendReq["TrackingToken"] = "";
                service.Execute(sendReq);
            }
            catch { }
        }

        private List<Guid> GetTeamMembers(IOrganizationService service, Guid teamId)
        {
            try
            {
                var qe = new QueryExpression("teammembership")
                {
                    ColumnSet = new ColumnSet("systemuserid"),
                    Criteria = new FilterExpression { Conditions = { new ConditionExpression("teamid", ConditionOperator.Equal, teamId) } }
                };
                return service.RetrieveMultiple(qe).Entities
                    .Where(e => e.Contains("systemuserid"))
                    .Select(e => e.GetAttributeValue<Guid>("systemuserid"))
                    .ToList();
            }
            catch { return new List<Guid>(); }
        }

        #endregion

        #region Validation

        private bool ValidateScheduler(StringBuilder errorLog, Entity source, string sourceEntity, string destEntity, Entity newEntity)
        {
            if (destEntity.Equals("msdyn_project", StringComparison.OrdinalIgnoreCase))
            {
                if (!newEntity.Contains("msdyn_scheduler")) { errorLog.AppendLine("msdyn_scheduler empty"); return false; }
                var osv = newEntity["msdyn_scheduler"] as OptionSetValue;
                if (osv?.Value != 192350002) { errorLog.AppendLine("msdyn_scheduler != 192350002"); return false; }
            }
            else if (destEntity.Equals("msdyn_projecttask", StringComparison.OrdinalIgnoreCase) && sourceEntity.Equals("msdyn_project", StringComparison.OrdinalIgnoreCase))
            {
                if (!source.Contains("msdyn_scheduler")) { errorLog.AppendLine("source msdyn_scheduler empty"); return false; }
                var osv = source["msdyn_scheduler"] as OptionSetValue;
                if (osv?.Value != 192350002) { errorLog.AppendLine("source msdyn_scheduler != 192350002"); return false; }
            }
            return true;
        }

        private bool ValidateIntegrationConditions(IOrganizationService service, Guid mappingId, Entity source, StringBuilder errorLog, Entity preImage, Dictionary<string, AttributeMetadata> metadataCache)
        {
            try
            {
                var qe = new QueryExpression("mfd_advancedintegrationcondition")
                {
                    ColumnSet = new ColumnSet("mfd_field", "mfd_condition", "mfd_value"),
                    Criteria = new FilterExpression { Conditions = { new ConditionExpression("mfd_advancedinegration", ConditionOperator.Equal, mappingId) } }
                };

                var conditions = service.RetrieveMultiple(qe).Entities;
                foreach (var cond in conditions)
                {
                    string field = cond.GetAttributeValue<string>("mfd_field");
                    int conditionType = cond.GetAttributeValue<OptionSetValue>("mfd_condition")?.Value ?? -1;
                    string expected = cond.GetAttributeValue<string>("mfd_value");

                    if (string.IsNullOrWhiteSpace(field)) continue;

                    object actual = source.Contains(field) ? source[field] : null;
                    object expectedTyped = ConvertToSafeType(service, source.LogicalName, field, expected, errorLog, metadataCache);

                    bool passed = false;
                    switch (conditionType)
                    {
                        case 1: passed = CompareValues(actual, expectedTyped); break;
                        case 2: passed = actual is string s && expectedTyped is string e && s.Contains(e); break;
                        case 3: passed = TryCompare(actual, expectedTyped, out var c1) && c1 >= 0; break;
                        case 4: passed = TryCompare(actual, expectedTyped, out var c2) && c2 <= 0; break;
                        case 5: passed = !CompareValues(actual, expectedTyped); break;
                        default: passed = true; break;
                    }

                    if (!passed) { errorLog.AppendLine($"Condition not Met: '{field}'"); return false; }
                }
                return true;
            }
            catch (Exception ex) { errorLog.AppendLine($"ValidateConditions error: {ex.Message}"); return false; }
        }

        /// [PERF] Lightweight companion to ValidateIntegrationConditions: fetches just the
        /// configured field names (same query, same "mfd_advancedinegration" filter) so the
        /// targeted source Retrieve in Execute() knows to include them - it needs this list
        /// before the source record it would use to evaluate the conditions even exists.
        /// Fail-open: any error returns an empty list, same as the rest of this file.
        private List<string> GetIntegrationConditionFields(IOrganizationService service, Guid mappingId, StringBuilder errorLog)
        {
            try
            {
                var qe = new QueryExpression("mfd_advancedintegrationcondition")
                {
                    ColumnSet = new ColumnSet("mfd_field"),
                    Criteria = new FilterExpression { Conditions = { new ConditionExpression("mfd_advancedinegration", ConditionOperator.Equal, mappingId) } }
                };

                return service.RetrieveMultiple(qe).Entities
                    .Select(e => e.GetAttributeValue<string>("mfd_field"))
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .ToList();
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[INTEGRATION CONDITION FIELDS ERROR] {ex.Message}");
                return new List<string>();
            }
        }

        #endregion

        #region Field Mappings

        private List<Entity> GetFieldMappings(IOrganizationService service, Guid mappingId, StringBuilder errorLog)
        {
            try
            {
                var qe = new QueryExpression("mfd_advancedintegrationmapping")
                {
                    ColumnSet = new ColumnSet(
                        "mfd_destinationcolumn",
                        "mfd_sourcecolumn",
                        "mfd_usedefaultvalue",
                        "mfd_defaultvalueifnonefound",
                        "mfd_usecondition"
                        //"mfd_userelatedentity",
                        //"mfd_lookupfield",
                        //"mfd_relatedentityfield"
                        ),
                    Criteria = new FilterExpression { Conditions = { new ConditionExpression("mfd_advancedintegration", ConditionOperator.Equal, mappingId) } }
                };
                return service.RetrieveMultiple(qe).Entities.ToList();
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[GET FIELD MAPPING ERROR] {ex.Message}");

                return new List<Entity>();
            }
        }

        #endregion

        #region Helpers

        private string FormatValueForLog(object value)
        {
            if (value == null) return "NULL";
            if (value is EntityReference er) return $"EntityRef({er.LogicalName}, {er.Id})";
            if (value is OptionSetValue osv) return $"OptionSet({osv.Value})";
            if (value is OptionSetValueCollection osvc) return $"MultiSelect([{string.Join(",", osvc.Select(o => o.Value))}])";
            if (value is Money m) return $"Money({m.Value})";
            if (value is DateTime dt) return $"DateTime({dt:yyyy-MM-dd HH:mm:ss})";
            if (value is bool b) return b ? "True" : "False";
            if (value is string s) return s.Length > 50 ? $"\"{s.Substring(0, 50)}...\"" : $"\"{s}\"";
            return value.ToString();
        }

        private string ExtractFieldFromError(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage)) return null;

            var patterns = new[]
            {
                @"attribute '(\w+)'",
                @"'(\w+)' attribute",
                @"field '(\w+)'",
                @"column '(\w+)'",
                @"'(\w+)' does not exist",
                @"attribute (\w+) ",
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(errorMessage, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                    return match.Groups[1].Value;
            }

            return null;
        }

        private Dictionary<string, object> FindFieldsByTypeInError(string errorMessage, Entity entity)
        {
            var result = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(errorMessage) || entity == null) return result;

            string errorLower = errorMessage.ToLower();

            foreach (var attr in entity.Attributes)
            {
                if (attr.Value == null) continue;

                bool isMatch = false;
                if ((errorLower.Contains("entityreference") || errorLower.Contains("entity reference")) && attr.Value is EntityReference) isMatch = true;
                else if (errorLower.Contains("double") && attr.Value is double) isMatch = true;
                else if (errorLower.Contains("decimal") && attr.Value is decimal) isMatch = true;
                else if ((errorLower.Contains("int32") || errorLower.Contains("integer")) && (attr.Value is int || attr.Value is Int32 || attr.Value is Int64)) isMatch = true;
                else if (errorLower.Contains("string") && attr.Value is string) isMatch = true;
                else if (errorLower.Contains("datetime") && attr.Value is DateTime) isMatch = true;
                else if (errorLower.Contains("boolean") && attr.Value is bool) isMatch = true;
                else if (errorLower.Contains("guid") && attr.Value is Guid) isMatch = true;
                else if (errorLower.Contains("money") && attr.Value is Money) isMatch = true;
                else if (errorLower.Contains("optionset") && attr.Value is OptionSetValue) isMatch = true;

                if (isMatch) result[attr.Key] = attr.Value;
            }

            return result;
        }

        private KeyValuePair<string, object>? FindFieldByEntityReferenceInError(string errorMessage, Entity entity)
        {
            if (string.IsNullOrEmpty(errorMessage) || entity == null) return null;

            var pattern = @"Entity '(\w+)'.*(?:Id|ID|id)\s*=\s*([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})";
            var match = System.Text.RegularExpressions.Regex.Match(errorMessage, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success && match.Groups.Count >= 3)
            {
                string entityName = match.Groups[1].Value.ToLower();
                string guidStr = match.Groups[2].Value;

                if (Guid.TryParse(guidStr, out Guid errorGuid))
                {
                    foreach (var attr in entity.Attributes)
                    {
                        if (attr.Value is EntityReference er)
                        {
                            if (er.LogicalName.Equals(entityName, StringComparison.OrdinalIgnoreCase) && er.Id == errorGuid)
                                return attr;
                        }
                    }
                }
            }

            return null;
        }

        private object ParseValueByDataType(string value, int dataType)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                switch (dataType)
                {
                    case DATATYPE_INTEGER: return int.TryParse(value, out int i) ? i : (object)value;
                    case DATATYPE_GUID: return Guid.TryParse(value, out Guid g) ? g : (object)value;
                    case DATATYPE_DECIMAL: return decimal.TryParse(value, out decimal d) ? d : (object)value;
                    case DATATYPE_BOOLEAN:
                        var n = value.Trim().ToLowerInvariant();
                        if (n == "yes" || n == "true" || n == "1" || n == "sí") return true;
                        if (n == "no" || n == "false" || n == "0") return false;
                        return value;
                    case DATATYPE_DATETIME: return DateTime.TryParse(value, out DateTime dt) ? dt : (object)value;
                    default: return value;
                }
            }
            catch { return value; }
        }

        private ConditionOperator GetConditionOperator(int op)
        {
            switch (op)
            {
                case OPERATOR_NOT_EQUAL: return ConditionOperator.NotEqual;
                case OPERATOR_CONTAINS: return ConditionOperator.Contains;
                case OPERATOR_GREATER_THAN: return ConditionOperator.GreaterThan;
                case OPERATOR_LESS_THAN: return ConditionOperator.LessThan;
                default: return ConditionOperator.Equal;
            }
        }

        private object ExtractQueryValue(object value, int dataType)
        {
            if (value == null) return null;

            object rawValue = value;
            if (value is OptionSetValue osv) rawValue = osv.Value;
            else if (value is Money m) rawValue = m.Value;
            else if (value is EntityReference er) rawValue = er.Id;

            try
            {
                switch (dataType)
                {
                    case DATATYPE_INTEGER:
                        return Convert.ToInt32(rawValue);
                    case DATATYPE_DECIMAL:
                        return Convert.ToDecimal(rawValue);
                    case DATATYPE_BOOLEAN:
                        return Convert.ToBoolean(rawValue);
                    case DATATYPE_DATETIME:
                        return Convert.ToDateTime(rawValue);
                    case DATATYPE_GUID:
                        if (rawValue is Guid) return rawValue;
                        if (rawValue is string strGuid) return Guid.Parse(strGuid);
                        break;
                }
            }
            catch
            {
                return rawValue;
            }

            return rawValue;
        }

        private string GetEnvironmentUrl(IOrganizationService service)
        {
            try
            {
                var qe = new QueryExpression("mfd_advancedintegrationconfiguration")
                {
                    ColumnSet = new ColumnSet("mfd_value"),
                    Criteria = new FilterExpression { Conditions = { new ConditionExpression("mfd_code", ConditionOperator.Equal, "EnvironmentURL") } }
                };
                return service.RetrieveMultiple(qe).Entities.FirstOrDefault()?.GetAttributeValue<string>("mfd_value");
            }
            catch { return null; }
        }

        private string TruncateIfNeeded(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength - 20) + "\n[TRUNCATED]";
        }

        private bool CompareValues(object actual, object expected)
        {
            if (actual == null && expected == null) return true;
            if (actual == null || expected == null) return false;
            if (actual is OptionSetValue oa && expected is OptionSetValue oe) return oa.Value == oe.Value;
            if (actual is OptionSetValue osva && expected is int ie) return osva.Value == ie;
            if (actual is EntityReference ea && expected is EntityReference ee) return ea.Id == ee.Id;
            if (actual is Money ma && expected is Money me) return ma.Value == me.Value;
            return Equals(actual, expected);
        }

        private bool TryCompare(object actual, object expected, out int result)
        {
            result = 0;
            if (actual == null || expected == null) return false;
            if (actual is OptionSetValue oa) actual = oa.Value;
            if (expected is OptionSetValue oe) expected = oe.Value;
            if (actual is decimal da && expected is decimal de) { result = da.CompareTo(de); return true; }
            if (actual is int ia && expected is int ie) { result = ia.CompareTo(ie); return true; }
            if (actual is DateTime dta && expected is DateTime dte) { result = dta.CompareTo(dte); return true; }
            if (decimal.TryParse(actual.ToString(), out decimal pa) && decimal.TryParse(expected.ToString(), out decimal pe)) { result = pa.CompareTo(pe); return true; }
            return false;
        }

        private object ConvertToSafeType(IOrganizationService service, string entityName, string column, object value, StringBuilder errorLog, Dictionary<string, AttributeMetadata> metadataCache)
        {
            try
            {
                if (value is AliasedValue av) value = av.Value;

                // [PERF] Metadata cache scoped to the current Execute() run. Before this,
                // every single field on every mapping/condition issued its own uncached
                // RetrieveAttributeRequest, on every execution - not only failed ones. Same
                // (entityName, column) is looked up repeatedly across field mappings,
                // mapping conditions and destination conditions, so this cuts a large,
                // easily-avoidable share of the metadata round trips per run.
                string cacheKey = entityName + "|" + column;
                AttributeMetadata metadata;
                if (metadataCache != null && metadataCache.TryGetValue(cacheKey, out AttributeMetadata cachedMetadata))
                {
                    metadata = cachedMetadata;
                }
                else
                {
                    try
                    {
                        var req = new RetrieveAttributeRequest { EntityLogicalName = entityName, LogicalName = column, RetrieveAsIfPublished = true };
                        metadata = ((RetrieveAttributeResponse)service.Execute(req)).AttributeMetadata;
                    }
                    catch
                    {
                        metadata = null;
                    }

                    // Cache a miss too (null), so a bad/renamed column name doesn't get
                    // re-queried on every field for the rest of this run.
                    if (metadataCache != null)
                        metadataCache[cacheKey] = metadata;
                }

                if (metadata == null) return value;

                if (metadata is PicklistAttributeMetadata || metadata is StatusAttributeMetadata || metadata is StateAttributeMetadata)
                {
                    if (value is double db) return new OptionSetValue(Convert.ToInt32(db));
                    if (value is string s && int.TryParse(s, out int i)) return new OptionSetValue(i);
                    if (value is int iv) return new OptionSetValue(iv);
                    if (value is OptionSetValue osv) return osv;
                }
                if (metadata is MultiSelectPicklistAttributeMetadata)
                {
                    if (value is string s)
                    {
                        var vals = s.Split(',').Select(x => x.Trim()).Where(x => int.TryParse(x, out _)).Select(x => new OptionSetValue(int.Parse(x))).ToList();
                        return new OptionSetValueCollection(vals);
                    }
                    if (value is OptionSetValueCollection osvc) return osvc;
                }
                if (metadata is LookupAttributeMetadata lm && lm.Targets.Any())
                {
                    if (value is string s && Guid.TryParse(s, out Guid g)) return new EntityReference(lm.Targets.First(), g);
                    if (value is EntityReference er) return er;
                    if (value is Guid gv) return new EntityReference(lm.Targets.First(), gv);
                }
                if (metadata is BooleanAttributeMetadata)
                {
                    if (value is string s)
                    {
                        var n = s.Trim().ToLowerInvariant();
                        if (n == "yes" || n == "true" || n == "sí" || n == "1") return true;
                        if (n == "no" || n == "false" || n == "0") return false;
                    }
                    if (value is bool b) return b;
                }
                if (metadata is DateTimeAttributeMetadata)
                {
                    if (value is string s && DateTime.TryParse(s, out DateTime dt)) return dt;
                    if (value is DateTime dtv) return dtv;
                }
                if (metadata is MoneyAttributeMetadata)
                {
                    if (value is double db) return new Money(Convert.ToDecimal(db));
                    if (value is string s && decimal.TryParse(s, out decimal d)) return new Money(d);
                    if (value is decimal dv) return new Money(dv);
                    if (value is Money m) return m;
                }
                if (metadata is DecimalAttributeMetadata)
                {
                    if (value is double db) return Convert.ToDecimal(db);
                    if (value is string s && decimal.TryParse(s, out decimal d)) return d;
                    if (value is decimal dv) return dv;
                }
                if (metadata is IntegerAttributeMetadata)
                {
                    if (value is double db) return Convert.ToInt32(db);
                    if (value is string s && int.TryParse(s, out int i)) return i;
                    if (value is int iv) return iv;
                }
                if (metadata is StringAttributeMetadata sm)
                {
                    string sv;
                    if (value is EntityReference erv) sv = erv.Id.ToString();
                    else if (value is OptionSetValue osvv) sv = osvv.Value.ToString();
                    else if (value is Money mv) sv = mv.Value.ToString();
                    else if (value is OptionSetValueCollection osvcv) sv = string.Join(",", osvcv.Select(o => o.Value));
                    else sv = value?.ToString() ?? "";

                    int max = sm.MaxLength ?? int.MaxValue;
                    return sv.Length > max ? sv.Substring(0, max) : sv;
                }
                if (metadata is DoubleAttributeMetadata)
                {
                    if (value is string s && double.TryParse(s, out double d)) return d;
                    if (value is decimal dv) return Convert.ToDouble(dv);
                    if (value is double dbv) return dbv;
                }

                return value;
            }
            catch (Exception ex)
            {
                errorLog.AppendLine("Error on ConvertToSafeType; error message: " + ex.Message);
                return value;
            }
        }

        private IPluginExecutionContext GetRootContext(IPluginExecutionContext context)
        {
            var current = context;
            while (current.ParentContext != null)
                current = current.ParentContext;
            return current;
        }

        #endregion
    }
}