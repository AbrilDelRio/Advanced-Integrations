using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace NGMM_AutoRegisterPlugin
{
    public class AutoRegisterPlugin : IPlugin
    {
        private const string ADVANCED_MAPPING_TABLE = "mfd_advancedmapping";
        private const string TRIGGER_TABLE = "mfd_advancedintegrationtrigger";
        private const string VALID_APPLICATION_ID = "PLUGIN100000";
        private const int EVENT_TYPE_UPDATE = 2;

        // Entities for which AdvancedIntegrationPlugin checks ngmm_integrationchangeorigincode
        // in the PreImage as a fallback (see Execute SAP loop check).
        private static readonly HashSet<string> SAP_LOOP_ENTITIES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "msdyn_workorder"
        };

        private const string SAP_ORIGIN_FIELD = "ngmm_integrationchangeorigincode";

        public void Execute(IServiceProvider serviceProvider)
        {
            IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);
            ITracingService tracer = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            string entityName = context.PrimaryEntityName;
            tracer.Trace($"AutoRegisterPlugin: {context.MessageName} on {entityName}");

            try
            {
                if (entityName == ADVANCED_MAPPING_TABLE)
                    HandleAdvancedMappingEvent(context, service);
                else if (entityName == TRIGGER_TABLE)
                    HandleTriggerEvent(context, service);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracer.Trace($"EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                throw new InvalidPluginExecutionException($"AutoRegisterPlugin error: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }

        #region Advanced Mapping Handlers

        private void HandleAdvancedMappingEvent(IPluginExecutionContext context, IOrganizationService service)
        {
            switch (context.MessageName)
            {
                case "Create": HandleMappingCreate(context, service); break;
                case "Update": HandleMappingUpdate(context, service); break;
                case "Delete": HandleMappingDelete(context, service); break;
            }
        }

        private void HandleMappingCreate(IPluginExecutionContext context, IOrganizationService service)
        {
            if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
                return;

            string applicationId = target.GetAttributeValue<string>("mfd_applicationid");
            if (applicationId != VALID_APPLICATION_ID)
                return;

            string sourceEntity = target.GetAttributeValue<string>("mfd_eventforsourceentity");
            OptionSetValue eventTypeOsv = target.GetAttributeValue<OptionSetValue>("mfd_eventtype");

            if (string.IsNullOrEmpty(sourceEntity) || eventTypeOsv == null)
                throw new InvalidPluginExecutionException("Event For Source Entity and Event Type are required");

            string messageName = GetMessageNameFromEventType(eventTypeOsv.Value);
            if (string.IsNullOrEmpty(messageName))
                throw new InvalidPluginExecutionException($"Invalid Event Type: {eventTypeOsv.Value}");

            bool isUpdate = eventTypeOsv.Value == EVENT_TYPE_UPDATE;
            RegisterPluginStep(service, sourceEntity, messageName, target.Id, startDisabled: isUpdate);
        }

        private void HandleMappingUpdate(IPluginExecutionContext context, IOrganizationService service)
        {
            if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
                return;
            if (!context.PreEntityImages.Contains("PreImage"))
                return;

            Entity preImage = context.PreEntityImages["PreImage"];

            string applicationId = target.Contains("mfd_applicationid")
                ? target.GetAttributeValue<string>("mfd_applicationid")
                : preImage.GetAttributeValue<string>("mfd_applicationid");

            if (applicationId != VALID_APPLICATION_ID)
                return;

            string oldStepId = preImage.GetAttributeValue<string>("mfd_pluggingregistrationid");
            string oldSourceEntity = preImage.GetAttributeValue<string>("mfd_eventforsourceentity");
            int? oldEventType = preImage.GetAttributeValue<OptionSetValue>("mfd_eventtype")?.Value;

            string newSourceEntity = target.Contains("mfd_eventforsourceentity")
                ? target.GetAttributeValue<string>("mfd_eventforsourceentity") : oldSourceEntity;
            int? newEventType = target.Contains("mfd_eventtype")
                ? target.GetAttributeValue<OptionSetValue>("mfd_eventtype")?.Value : oldEventType;

            bool wroteMetadata = false;

            // Handle mfd_enabled toggle
            if (target.Contains("mfd_enabled"))
            {
                bool nowEnabled = target.GetAttributeValue<bool>("mfd_enabled");

                if (!string.IsNullOrEmpty(oldStepId) && Guid.TryParse(oldStepId, out Guid enableStepId))
                {
                    if (!nowEnabled)
                    {
                        SetStepState(service, enableStepId, enabled: false);
                        wroteMetadata = true;
                    }
                    else
                    {
                        bool isUpdateType = (newEventType ?? oldEventType) == EVENT_TYPE_UPDATE;
                        if (!isUpdateType)
                        {
                            SetStepState(service, enableStepId, enabled: true);
                            wroteMetadata = true;
                        }
                        else
                        {
                            string existingColumns = GetAllTriggerColumns(service, target.Id);
                            SetStepState(service, enableStepId, enabled: !string.IsNullOrEmpty(existingColumns));
                            wroteMetadata = true;
                        }
                    }
                }
            }

            // Nothing changed in source/type
            if (oldSourceEntity == newSourceEntity && oldEventType == newEventType)
            {
                // Self-healing: sync PreImage attributes to minimal list on any mapping edit.
                // Skip if we already wrote metadata in this execution (SetStepState) — Dataverse
                // does not allow multiple metadata writes in the same plugin transaction.
                if (!wroteMetadata && !string.IsNullOrEmpty(oldStepId) && Guid.TryParse(oldStepId, out Guid existingStepId))
                {
                    string message = GetMessageNameFromEventType(oldEventType ?? 0);
                    if (message == "Update" || message == "Delete")
                        SyncPreImageAttributes(service, existingStepId, target.Id, oldSourceEntity);
                }
                return;
            }

            // Source entity or event type changed — delete old step and register a new one
            if (!string.IsNullOrEmpty(oldStepId) && Guid.TryParse(oldStepId, out Guid stepGuid))
                DeletePluginStep(service, stepGuid);

            string messageName = GetMessageNameFromEventType(newEventType ?? 0);
            if (!string.IsNullOrEmpty(messageName) && !string.IsNullOrEmpty(newSourceEntity))
            {
                bool currentlyEnabled = target.Contains("mfd_enabled")
                    ? target.GetAttributeValue<bool>("mfd_enabled")
                    : preImage.GetAttributeValue<bool?>("mfd_enabled") ?? true;

                bool isUpdate = newEventType == EVENT_TYPE_UPDATE;
                bool startDisabled = !currentlyEnabled || isUpdate;
                RegisterPluginStep(service, newSourceEntity, messageName, target.Id, startDisabled: startDisabled);
                ReapplyTriggers(service, target.Id, newSourceEntity);
            }
        }

        private void HandleMappingDelete(IPluginExecutionContext context, IOrganizationService service)
        {
            if (!context.PreEntityImages.Contains("PreImage"))
                return;

            Entity preImage = context.PreEntityImages["PreImage"];

            string applicationId = preImage.GetAttributeValue<string>("mfd_applicationid");
            if (applicationId != VALID_APPLICATION_ID)
                return;

            string stepIdStr = preImage.GetAttributeValue<string>("mfd_pluggingregistrationid");

            if (!string.IsNullOrEmpty(stepIdStr) && Guid.TryParse(stepIdStr, out Guid stepId))
                DeletePluginStep(service, stepId);
        }

        private void ReapplyTriggers(IOrganizationService service, Guid mappingId, string sourceEntityName = null)
        {
            Guid? stepId = GetStepIdFromMapping(service, mappingId);
            if (!stepId.HasValue) return;

            string allColumns = GetAllTriggerColumns(service, mappingId);
            if (!string.IsNullOrEmpty(allColumns))
            {
                Entity updateStep = new Entity("sdkmessageprocessingstep", stepId.Value);
                updateStep["filteringattributes"] = allColumns;
                service.Update(updateStep);
            }

            if (string.IsNullOrEmpty(sourceEntityName))
                sourceEntityName = GetSourceEntityFromMapping(service, mappingId);
            SyncPreImageAttributes(service, stepId.Value, mappingId, sourceEntityName);

            if (IsMappingUpdateType(service, mappingId))
                SetStepState(service, stepId.Value, enabled: !string.IsNullOrEmpty(allColumns));
        }

        #endregion

        #region Trigger Handlers

        private void HandleTriggerEvent(IPluginExecutionContext context, IOrganizationService service)
        {
            switch (context.MessageName)
            {
                case "Create": HandleTriggerCreate(context, service); break;
                case "Update": HandleTriggerUpdate(context, service); break;
                case "Delete": HandleTriggerDelete(context, service); break;
            }
        }

        private void HandleTriggerCreate(IPluginExecutionContext context, IOrganizationService service)
        {
            if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
                return;

            string columnName = target.GetAttributeValue<string>("mfd_columnname");
            EntityReference mappingRef = target.GetAttributeValue<EntityReference>("mfd_advancedintegration");

            if (string.IsNullOrEmpty(columnName) || mappingRef == null)
                return;

            if (!IsValidMapping(service, mappingRef.Id))
                return;

            Guid? stepId = GetStepIdFromMapping(service, mappingRef.Id);
            if (!stepId.HasValue) return;

            AddFilteringAttribute(service, stepId.Value, columnName);

            string sourceEntity = GetSourceEntityFromMapping(service, mappingRef.Id);
            SyncPreImageAttributes(service, stepId.Value, mappingRef.Id, sourceEntity);

            if (IsMappingUpdateType(service, mappingRef.Id))
                SetStepState(service, stepId.Value, enabled: true);
        }

        private void HandleTriggerUpdate(IPluginExecutionContext context, IOrganizationService service)
        {
            if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
                return;
            if (!target.Contains("mfd_columnname") || !context.PreEntityImages.Contains("PreImage"))
                return;

            Entity preImage = context.PreEntityImages["PreImage"];
            string oldColumn = preImage.GetAttributeValue<string>("mfd_columnname");
            string newColumn = target.GetAttributeValue<string>("mfd_columnname");
            EntityReference mappingRef = preImage.GetAttributeValue<EntityReference>("mfd_advancedintegration");

            if (mappingRef == null) return;

            if (!IsValidMapping(service, mappingRef.Id))
                return;

            Guid? stepId = GetStepIdFromMapping(service, mappingRef.Id);
            if (!stepId.HasValue) return;

            if (!string.IsNullOrEmpty(oldColumn))
                RemoveFilteringAttribute(service, stepId.Value, oldColumn);
            if (!string.IsNullOrEmpty(newColumn))
                AddFilteringAttribute(service, stepId.Value, newColumn);

            string sourceEntity = GetSourceEntityFromMapping(service, mappingRef.Id);
            SyncPreImageAttributes(service, stepId.Value, mappingRef.Id, sourceEntity);
        }

        private void HandleTriggerDelete(IPluginExecutionContext context, IOrganizationService service)
        {
            if (!context.PreEntityImages.Contains("PreImage"))
                return;

            Entity preImage = context.PreEntityImages["PreImage"];
            string columnName = preImage.GetAttributeValue<string>("mfd_columnname");
            EntityReference mappingRef = preImage.GetAttributeValue<EntityReference>("mfd_advancedintegration");

            if (string.IsNullOrEmpty(columnName) || mappingRef == null)
                return;

            if (!IsValidMapping(service, mappingRef.Id))
                return;

            Guid? stepId = GetStepIdFromMapping(service, mappingRef.Id);
            if (!stepId.HasValue) return;

            RemoveFilteringAttribute(service, stepId.Value, columnName);

            // Exclude the record being deleted (still in DB in PreOperation)
            string sourceEntity = GetSourceEntityFromMapping(service, mappingRef.Id);
            SyncPreImageAttributesExcluding(service, stepId.Value, mappingRef.Id, sourceEntity, context.PrimaryEntityId);

            if (IsMappingUpdateType(service, mappingRef.Id))
            {
                string remainingColumns = GetAllTriggerColumnsExcluding(service, mappingRef.Id, context.PrimaryEntityId);
                if (string.IsNullOrEmpty(remainingColumns))
                    SetStepState(service, stepId.Value, enabled: false);
            }
        }

        private bool IsValidMapping(IOrganizationService service, Guid mappingId)
        {
            Entity mapping = service.Retrieve(ADVANCED_MAPPING_TABLE, mappingId, new ColumnSet("mfd_applicationid"));
            string appId = mapping.GetAttributeValue<string>("mfd_applicationid");
            return appId == VALID_APPLICATION_ID;
        }

        private bool IsMappingUpdateType(IOrganizationService service, Guid mappingId)
        {
            Entity mapping = service.Retrieve(ADVANCED_MAPPING_TABLE, mappingId, new ColumnSet("mfd_eventtype"));
            return mapping.GetAttributeValue<OptionSetValue>("mfd_eventtype")?.Value == EVENT_TYPE_UPDATE;
        }

        private string GetSourceEntityFromMapping(IOrganizationService service, Guid mappingId)
        {
            Entity mapping = service.Retrieve(ADVANCED_MAPPING_TABLE, mappingId, new ColumnSet("mfd_eventforsourceentity"));
            return mapping.GetAttributeValue<string>("mfd_eventforsourceentity");
        }

        #endregion

        #region Filtering Attributes

        private void AddFilteringAttribute(IOrganizationService service, Guid stepId, string columnName)
        {
            Entity step = service.Retrieve("sdkmessageprocessingstep", stepId, new ColumnSet("filteringattributes"));
            string current = step.GetAttributeValue<string>("filteringattributes") ?? "";

            List<string> attrs = string.IsNullOrEmpty(current) ? new List<string>()
                : current.Split(',').Select(a => a.Trim().ToLower()).Where(a => !string.IsNullOrEmpty(a)).ToList();

            string normalized = columnName.Trim().ToLower();
            if (!attrs.Contains(normalized))
            {
                attrs.Add(normalized);
                Entity update = new Entity("sdkmessageprocessingstep", stepId);
                update["filteringattributes"] = string.Join(",", attrs);
                service.Update(update);
            }
        }

        private void RemoveFilteringAttribute(IOrganizationService service, Guid stepId, string columnName)
        {
            Entity step = service.Retrieve("sdkmessageprocessingstep", stepId, new ColumnSet("filteringattributes"));
            string current = step.GetAttributeValue<string>("filteringattributes") ?? "";
            if (string.IsNullOrEmpty(current)) return;

            List<string> attrs = current.Split(',').Select(a => a.Trim().ToLower()).Where(a => !string.IsNullOrEmpty(a)).ToList();
            string normalized = columnName.Trim().ToLower();

            if (attrs.Contains(normalized))
            {
                attrs.Remove(normalized);
                Entity update = new Entity("sdkmessageprocessingstep", stepId);
                update["filteringattributes"] = string.Join(",", attrs);
                service.Update(update);
            }
        }

        private string GetAllTriggerColumns(IOrganizationService service, Guid mappingId)
        {
            return GetAllTriggerColumnsExcluding(service, mappingId, Guid.Empty);
        }

        private string GetAllTriggerColumnsExcluding(IOrganizationService service, Guid mappingId, Guid excludeTriggerRecordId)
        {
            var qe = new QueryExpression(TRIGGER_TABLE)
            {
                ColumnSet = new ColumnSet("mfd_columnname"),
                Criteria = new FilterExpression(LogicalOperator.And)
                {
                    Conditions =
                    {
                        new ConditionExpression("mfd_advancedintegration", ConditionOperator.Equal, mappingId)
                    }
                }
            };

            if (excludeTriggerRecordId != Guid.Empty)
                qe.Criteria.AddCondition(TRIGGER_TABLE + "id", ConditionOperator.NotEqual, excludeTriggerRecordId);

            return string.Join(",", service.RetrieveMultiple(qe).Entities
                .Select(t => t.GetAttributeValue<string>("mfd_columnname"))
                .Where(c => !string.IsNullOrEmpty(c))
                .Select(c => c.Trim().ToLower())
                .Distinct());
        }

        #endregion

        #region PreImage Attribute Sync

        /// <summary>
        /// Builds the minimal set of attributes AdvancedIntegrationPlugin actually reads
        /// from the PreImage:
        ///   1. All trigger columns (for HasValueChanged change detection)
        ///   2. ngmm_integrationchangeorigincode, only for msdyn_workorder / msdyn_projecttask
        /// </summary>
        private string BuildPreImageAttributes(IOrganizationService service, Guid mappingId, string sourceEntityName, Guid excludeTriggerRecordId = default)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string triggerCols = excludeTriggerRecordId == Guid.Empty
                ? GetAllTriggerColumns(service, mappingId)
                : GetAllTriggerColumnsExcluding(service, mappingId, excludeTriggerRecordId);

            if (!string.IsNullOrEmpty(triggerCols))
            {
                foreach (var col in triggerCols.Split(','))
                {
                    string c = col.Trim().ToLower();
                    if (!string.IsNullOrEmpty(c))
                        columns.Add(c);
                }
            }

            if (!string.IsNullOrEmpty(sourceEntityName) && SAP_LOOP_ENTITIES.Contains(sourceEntityName))
                columns.Add(SAP_ORIGIN_FIELD.ToLower());

            return string.Join(",", columns.OrderBy(c => c));
        }

        private void SyncPreImageAttributes(IOrganizationService service, Guid stepId, Guid mappingId, string sourceEntityName)
        {
            SyncPreImageAttributesExcluding(service, stepId, mappingId, sourceEntityName, Guid.Empty);
        }

        private void SyncPreImageAttributesExcluding(IOrganizationService service, Guid stepId, Guid mappingId, string sourceEntityName, Guid excludeTriggerRecordId)
        {
            string newAttributes = BuildPreImageAttributes(service, mappingId, sourceEntityName, excludeTriggerRecordId);

            // Dataverse rejects attributes="" on a PreImage — skip if no columns to set
            if (string.IsNullOrEmpty(newAttributes))
                return;

            var imageQuery = new QueryExpression("sdkmessageprocessingstepimage")
            {
                ColumnSet = new ColumnSet("sdkmessageprocessingstepimageid", "attributes", "name", "entityalias", "imagetype", "messagepropertyname"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("sdkmessageprocessingstepid", ConditionOperator.Equal, stepId),
                        new ConditionExpression("imagetype", ConditionOperator.Equal, 0) // PreImage
                    }
                }
            };

            foreach (var img in service.RetrieveMultiple(imageQuery).Entities)
            {
                string current = img.GetAttributeValue<string>("attributes") ?? "";
                if (string.Equals(current, newAttributes, StringComparison.OrdinalIgnoreCase)) continue;

                // Managed components can't be updated — delete and recreate
                string name = img.GetAttributeValue<string>("name") ?? "PreImage";
                string alias = img.GetAttributeValue<string>("entityalias") ?? "PreImage";
                string msgProp = img.GetAttributeValue<string>("messagepropertyname") ?? "Target";

                service.Delete("sdkmessageprocessingstepimage", img.Id);

                Entity newImg = new Entity("sdkmessageprocessingstepimage");
                newImg["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId);
                newImg["name"] = name;
                newImg["entityalias"] = alias;
                newImg["imagetype"] = new OptionSetValue(0);
                newImg["messagepropertyname"] = msgProp;
                newImg["attributes"] = newAttributes;
                service.Create(newImg);
            }
        }

        #endregion

        #region Step State

        /// <summary>
        /// Enables or disables a plugin step.
        /// StateCode 0 = Enabled, StateCode 1 = Disabled.
        /// StatusCode 1 = Enabled, StatusCode 2 = Disabled.
        /// Tries SetStateRequest first; falls back to direct Update.
        /// </summary>
        private void SetStepState(IOrganizationService service, Guid stepId, bool enabled)
        {
            try
            {
                var setState = new Microsoft.Crm.Sdk.Messages.SetStateRequest
                {
                    EntityMoniker = new EntityReference("sdkmessageprocessingstep", stepId),
                    State = new OptionSetValue(enabled ? 0 : 1),
                    Status = new OptionSetValue(enabled ? 1 : 2)
                };
                service.Execute(setState);
                return;
            }
            catch (InvalidPluginExecutionException) { throw; }
            catch
            {
                // SetStateRequest not available in this context — fall back to direct update
            }

            Entity step = new Entity("sdkmessageprocessingstep", stepId);
            step["statecode"] = new OptionSetValue(enabled ? 0 : 1);
            step["statuscode"] = new OptionSetValue(enabled ? 1 : 2);
            service.Update(step);
        }

        #endregion

        #region Step Registration

        private void RegisterPluginStep(IOrganizationService service, string entityName, string messageName, Guid mappingId, bool startDisabled = false)
        {
            if (messageName == "Manual") return;

            var pluginQuery = new QueryExpression("plugintype")
            {
                ColumnSet = new ColumnSet("plugintypeid"),
                Criteria = new FilterExpression { Conditions = { new ConditionExpression("typename", ConditionOperator.Equal, "CreateAdvancedIntegrationPlugin.CreateAdvancedIntegrationPlugin") } }
            };

            var pluginTypes = service.RetrieveMultiple(pluginQuery);
            if (pluginTypes.Entities.Count == 0)
                throw new InvalidPluginExecutionException("Plugin type not found");

            Guid pluginTypeId = pluginTypes.Entities.First().Id;
            Guid messageId = GetMessageId(service, messageName);
            Guid messageFilterId = GetMessageFilterId(service, messageName, entityName);

            Entity step = new Entity("sdkmessageprocessingstep");
            step["name"] = $"AutoReg: {messageName} on {entityName} - {mappingId}";
            step["plugintypeid"] = new EntityReference("plugintype", pluginTypeId);
            step["sdkmessageid"] = new EntityReference("sdkmessage", messageId);
            step["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", messageFilterId);
            step["stage"] = new OptionSetValue(40);
            step["mode"] = new OptionSetValue(1);
            step["rank"] = 1;
            step["supporteddeployment"] = new OptionSetValue(0);
            step["asyncautodelete"] = true;
            step["filteringattributes"] = "";
            step["configuration"] = mappingId.ToString();

            Guid stepId = service.Create(step);

            if (startDisabled)
                SetStepState(service, stepId, enabled: false);

            if (messageName == "Update" || messageName == "Delete")
            {
                // At creation time there are no triggers yet — start with only the SAP origin
                // field for entities that need it. Trigger columns are added incrementally
                // by SyncPreImageAttributes each time a trigger record is created.
                string preImageAttributes = BuildPreImageAttributes(service, mappingId, entityName);

                Entity preImage = new Entity("sdkmessageprocessingstepimage");
                preImage["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId);
                preImage["name"] = "PreImage";
                preImage["entityalias"] = "PreImage";
                preImage["imagetype"] = new OptionSetValue(0);
                preImage["messagepropertyname"] = messageName == "Delete" ? "Id" : "Target";
                preImage["attributes"] = preImageAttributes;
                service.Create(preImage);
            }

            Entity updateMapping = new Entity(ADVANCED_MAPPING_TABLE, mappingId);
            updateMapping["mfd_pluggingregistrationid"] = stepId.ToString();
            service.Update(updateMapping);
        }

        private void DeletePluginStep(IOrganizationService service, Guid stepId)
        {
            // Verify the step exists before trying to delete it
            try { service.Retrieve("sdkmessageprocessingstep", stepId, new ColumnSet(false)); }
            catch { return; } // Step doesn't exist — nothing to delete, not an error

            var imageQuery = new QueryExpression("sdkmessageprocessingstepimage")
            {
                ColumnSet = new ColumnSet(false),
                Criteria = new FilterExpression { Conditions = { new ConditionExpression("sdkmessageprocessingstepid", ConditionOperator.Equal, stepId) } }
            };

            foreach (var image in service.RetrieveMultiple(imageQuery).Entities)
                service.Delete("sdkmessageprocessingstepimage", image.Id);

            service.Delete("sdkmessageprocessingstep", stepId);
        }

        #endregion

        #region Helpers

        private Guid? GetStepIdFromMapping(IOrganizationService service, Guid mappingId)
        {
            Entity mapping = service.Retrieve(ADVANCED_MAPPING_TABLE, mappingId, new ColumnSet("mfd_pluggingregistrationid"));
            string stepIdStr = mapping.GetAttributeValue<string>("mfd_pluggingregistrationid");
            if (!string.IsNullOrEmpty(stepIdStr) && Guid.TryParse(stepIdStr, out Guid stepId))
                return stepId;
            return null;
        }

        private string GetMessageNameFromEventType(int eventType)
        {
            switch (eventType)
            {
                case 1: return "Create";
                case 2: return "Update";
                case 3: return "Delete";
                case 4: return "Manual";
                default: return null;
            }
        }

        private Guid GetMessageId(IOrganizationService service, string messageName)
        {
            var query = new QueryExpression("sdkmessage")
            {
                ColumnSet = new ColumnSet("sdkmessageid"),
                Criteria = new FilterExpression { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, messageName) } }
            };
            var results = service.RetrieveMultiple(query);
            if (results.Entities.Count == 0) throw new InvalidPluginExecutionException($"Message not found: {messageName}");
            return results.Entities.First().Id;
        }

        private Guid GetMessageFilterId(IOrganizationService service, string messageName, string entityName)
        {
            var query = new QueryExpression("sdkmessagefilter")
            {
                ColumnSet = new ColumnSet("sdkmessagefilterid"),
                Criteria = new FilterExpression { Conditions = { new ConditionExpression("primaryobjecttypecode", ConditionOperator.Equal, entityName) } },
                LinkEntities = {
                    new LinkEntity {
                        LinkFromEntityName = "sdkmessagefilter", LinkToEntityName = "sdkmessage",
                        LinkFromAttributeName = "sdkmessageid", LinkToAttributeName = "sdkmessageid",
                        LinkCriteria = new FilterExpression { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, messageName) } }
                    }
                }
            };
            var results = service.RetrieveMultiple(query);
            if (results.Entities.Count == 0) throw new InvalidPluginExecutionException($"Message filter not found: {messageName} on {entityName}");
            return results.Entities.First().Id;
        }

        #endregion
    }
}