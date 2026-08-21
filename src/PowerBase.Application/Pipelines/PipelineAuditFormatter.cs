using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

public class PipelineAuditFormatter : IPipelineAuditFormatter
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IUserRepository _userRepo;
    private readonly IRecordRepository _recordRepo;

    // Cache objects to prevent N+1 queries
    private string _pipelineName = string.Empty;
    private string _appName = string.Empty;
    private Guid _pipelinePublicId = Guid.Empty;
    private string _triggeredByUserDisplayName = "System";
    private Dictionary<string, object?> _triggeredByUserMetadata = new();
    private readonly Dictionary<Guid, (AppTable Table, List<AppField> Fields)> _metadataCache = new();
    private readonly Dictionary<Guid, string> _connectionCache = new();
    private readonly Dictionary<Guid, string> _recordDisplayCache = new();
    private readonly Dictionary<long, string> _stepLabelCache = new();

    public PipelineAuditFormatter(
        IPipelineRepository pipelineRepo,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IUserRepository userRepo,
        IRecordRepository recordRepo)
    {
        _pipelineRepo = pipelineRepo;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _userRepo = userRepo;
        _recordRepo = recordRepo;
    }

    public async Task InitializeAsync(long pipelineId, long triggeredByUserId, CancellationToken ct)
    {
        // 1. Fetch Pipeline Name and App ID
        var pipeline = await _pipelineRepo.GetByIdAsync(pipelineId, ct);
        if (pipeline != null)
        {
            _pipelineName = pipeline.Name;
            _pipelinePublicId = pipeline.PublicId;
            
            // 2. Fetch App Name
            try
            {
                var appPublicId = await _appRepo.GetPublicIdByIdAsync(pipeline.AppId, ct);
                var app = await _appRepo.GetByPublicIdAsync(appPublicId, ct);
                if (app != null)
                {
                    _appName = app.Name;
                }
            }
            catch {}
        }

        // 3. Fetch Triggered By User Details
        if (triggeredByUserId != 0)
        {
            try
            {
                var user = await _userRepo.GetByIdAsync(triggeredByUserId, ct);
                if (user != null)
                {
                    _triggeredByUserDisplayName = !string.IsNullOrWhiteSpace(user.Name) ? user.Name : user.Email;
                    _triggeredByUserMetadata = new Dictionary<string, object?>
                    {
                        { "id", user.PublicId.ToString() },
                        { "firstName", GetFirstName(user.Name) },
                        { "lastName", GetLastName(user.Name) },
                        { "email", user.Email },
                        { "screenName", user.Name }
                    };
                }
            }
            catch {}
        }

        // 4. Pre-fetch Pipeline Steps to cache Step Labels & RefIds
        try
        {
            var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, ct);
            foreach (var s in steps)
            {
                _stepLabelCache[s.Id] = !string.IsNullOrWhiteSpace(s.Label) ? s.Label : s.RefId;
            }
        }
        catch {}

        // 5. Pre-fetch connections for this pipeline
        try
        {
            var connections = await _pipelineRepo.GetConnectionsByPipelineIdAsync(pipelineId, ct);
            foreach (var conn in connections)
            {
                _connectionCache[conn.PublicId] = conn.Name;
            }
        }
        catch {}

        // 6. Pre-fetch Tables referenced in step configs
        try
        {
            var steps = await _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, ct);
            var tableGuids = new HashSet<Guid>();
            foreach (var step in steps)
            {
                if (!string.IsNullOrWhiteSpace(step.ConfigJson))
                {
                    using var doc = JsonDocument.Parse(step.ConfigJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("tableId", out var tProp) && tProp.ValueKind == JsonValueKind.String && Guid.TryParse(tProp.GetString(), out var tId))
                            tableGuids.Add(tId);
                        if (doc.RootElement.TryGetProperty("tableLabel", out var tlProp) && tlProp.ValueKind == JsonValueKind.String && Guid.TryParse(tlProp.GetString(), out var tlId))
                            tableGuids.Add(tlId);
                        if (doc.RootElement.TryGetProperty("tablePublicId", out var tpProp) && tpProp.ValueKind == JsonValueKind.String && Guid.TryParse(tpProp.GetString(), out var tpId))
                            tableGuids.Add(tpId);
                    }
                }
            }

            foreach (var tGuid in tableGuids)
            {
                await GetOrFetchTableMetadataAsync(tGuid, ct);
            }
        }
        catch {}
    }

    private static string GetFirstName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
        var parts = fullName.Trim().Split(' ');
        return parts[0];
    }

    private static string GetLastName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
        var parts = fullName.Trim().Split(' ');
        return parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : string.Empty;
    }

    private async Task<(AppTable Table, List<AppField> Fields)?> GetOrFetchTableMetadataAsync(Guid tableGuid, CancellationToken ct)
    {
        if (_metadataCache.TryGetValue(tableGuid, out var cached))
            return cached;

        try
        {
            using (var suppressScope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
            {
                var table = await _tableRepo.GetByPublicIdAsync(tableGuid, ct);
                if (table != null)
                {
                    var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
                    var entry = (table, fields.ToList());
                    _metadataCache[tableGuid] = entry;
                    suppressScope.Complete();
                    return entry;
                }
            }
        }
        catch {}

        return null;
    }

    private async Task<string> GetOrFetchConnectionNameAsync(Guid connGuid, CancellationToken ct)
    {
        if (_connectionCache.TryGetValue(connGuid, out var cached))
            return cached;

        try
        {
            using (var suppressScope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
            {
                var conn = await _pipelineRepo.GetConnectionByPublicIdAsync(connGuid, ct);
                if (conn != null)
                {
                    _connectionCache[connGuid] = conn.Name;
                    suppressScope.Complete();
                    return conn.Name;
                }
            }
        }
        catch {}

        return connGuid.ToString();
    }

    private async Task<string> GetOrFetchRecordDisplayAsync(AppTable table, List<AppField> fields, Guid recordPublicId, CancellationToken ct)
    {
        if (_recordDisplayCache.TryGetValue(recordPublicId, out var cached))
            return cached;

        try
        {
            using (var suppressScope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
            {
                var record = await _recordRepo.GetByPublicIdAsync(table, fields, recordPublicId, ct);
                if (record != null)
                {
                    var valuesDict = new Dictionary<string, object?>();
                    foreach (var field in fields)
                    {
                        if (field.Fid.HasValue)
                        {
                            var colKey = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(field);
                            if (record.TryGetValue(colKey, out var val))
                            {
                                valuesDict[$"fid_{field.Fid.Value}"] = val;
                            }
                        }
                    }
                    var displayName = GetRecordDisplayValue(table, fields, valuesDict, recordPublicId.ToString());
                    _recordDisplayCache[recordPublicId] = displayName;
                    suppressScope.Complete();
                    return displayName;
                }
            }
        }
        catch {}

        return recordPublicId.ToString();
    }

    public (string InputContextJson, string OutputContextJson, string LogMessage) FormatStepRun(
        PipelineStep step,
        string? rawInputJson,
        string? rawOutputJson,
        string status,
        string correlationId,
        DateTime? startedOn,
        DateTime? completedOn)
    {
        var ct = CancellationToken.None;
        var subtype = step.Subtype?.ToLowerInvariant();
        var type = step.Type?.ToLowerInvariant();

        var header = new Dictionary<string, object?>();
        var friendlyInput = new Dictionary<string, object?>();
        var friendlyOutput = new Dictionary<string, object?>();
        var metadata = new Dictionary<string, object?>();
        var technicalDetails = new Dictionary<string, object?>();
        string logMessage = string.Empty;

        // Base run timestamp formatting (ISO-8601 UTC)
        var runTimestamp = startedOn.HasValue 
            ? startedOn.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") 
            : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Set baseline Header
        header["Type"] = step.Subtype ?? step.Type;
        header["Channel"] = "powerbase";
        header["PipelineName"] = _pipelineName;
        header["StepName"] = !string.IsNullOrWhiteSpace(step.Label) ? step.Label : step.RefId;
        header["ReferenceId"] = step.RefId;
        header["RunTimestamp"] = runTimestamp;
        header["Status"] = status;

        // Set baseline Technical Details
        technicalDetails["CorrelationId"] = correlationId;
        technicalDetails["StepId"] = step.Id;
        technicalDetails["StepRefId"] = step.RefId;
        technicalDetails["StepType"] = step.Type;
        technicalDetails["StepSubtype"] = step.Subtype;
        technicalDetails["Status"] = status;

        if (startedOn.HasValue)
        {
            technicalDetails["StartedAt"] = startedOn.Value.ToString("o");
            technicalDetails["StartedAtEpochMs"] = new DateTimeOffset(startedOn.Value.ToUniversalTime()).ToUnixTimeMilliseconds();
        }
        if (completedOn.HasValue)
        {
            technicalDetails["CompletedAt"] = completedOn.Value.ToString("o");
            technicalDetails["CompletedAtEpochMs"] = new DateTimeOffset(completedOn.Value.ToUniversalTime()).ToUnixTimeMilliseconds();
        }

        try
        {
            var inputDict = DeserializeJsonToDict(rawInputJson);
            var outputDict = DeserializeJsonToDict(rawOutputJson);

            if (type == "trigger" && subtype == "new-event")
            {
                var eventTypeStr = inputDict.TryGetValue("EventType", out var et) ? et?.ToString() : "Added";
                var eventLabel = eventTypeStr switch
                {
                    "Added" => "Record Added",
                    "Modified" => "Record Updated",
                    "Deleted" => "Record Deleted",
                    _ => eventTypeStr ?? "Record Added"
                };

                var tableGuidStr = inputDict.TryGetValue("TablePublicId", out var tIdObj) ? tIdObj?.ToString() : null;
                var recordGuidStr = inputDict.TryGetValue("RecordPublicId", out var rIdObj) ? rIdObj?.ToString() : null;
                var connectionGuidStr = inputDict.TryGetValue("ConnectionPublicId", out var cIdObj) ? cIdObj?.ToString() : null;

                string tableName = "Table";
                string connectionName = "Default Connection";
                string recordDisplayName = recordGuidStr ?? "Record";

                List<AppField> fields = new();
                AppTable? tableMeta = null;
                if (!string.IsNullOrEmpty(tableGuidStr) && Guid.TryParse(tableGuidStr, out var tGuid))
                {
                    var meta = GetOrFetchTableMetadataAsync(tGuid, ct).GetAwaiter().GetResult();
                    if (meta != null)
                    {
                        tableMeta = meta.Value.Table;
                        tableName = tableMeta.Name;
                        fields = meta.Value.Fields;

                        if (!string.IsNullOrEmpty(recordGuidStr) && Guid.TryParse(recordGuidStr, out var rGuid))
                        {
                            if (eventTypeStr == "Deleted" && inputDict.TryGetValue("OldValues", out var oldValObj) && oldValObj != null)
                            {
                                var oldValuesDict = AsDictionary(oldValObj);
                                recordDisplayName = GetRecordDisplayValue(tableMeta, fields, oldValuesDict, recordGuidStr);
                            }
                            else if (inputDict.TryGetValue("NewValues", out var newValObj) && newValObj != null)
                            {
                                var newValuesDict = AsDictionary(newValObj);
                                recordDisplayName = GetRecordDisplayValue(tableMeta, fields, newValuesDict, recordGuidStr);
                            }
                            else
                            {
                                recordDisplayName = GetOrFetchRecordDisplayAsync(tableMeta, fields, rGuid, ct).GetAwaiter().GetResult();
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(connectionGuidStr) && Guid.TryParse(connectionGuidStr, out var cGuid))
                {
                    connectionName = GetOrFetchConnectionNameAsync(cGuid, ct).GetAwaiter().GetResult();
                }

                // Configuration details from step ConfigJson
                bool triggerOnAdded = false;
                bool triggerOnModified = false;
                bool triggerOnDeleted = false;
                var exportFields = new List<string>();
                if (!string.IsNullOrWhiteSpace(step.ConfigJson))
                {
                    try
                    {
                        var config = JsonSerializer.Deserialize<NewEventStepConfig>(step.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (config != null)
                        {
                            triggerOnAdded = config.TriggerOnAdded;
                            triggerOnModified = config.TriggerOnModified;
                            triggerOnDeleted = config.TriggerOnDeleted;
                            if (config.SubsequentFields != null)
                            {
                                foreach (var fStr in config.SubsequentFields)
                                {
                                    if (!string.IsNullOrEmpty(fStr))
                                    {
                                        var matched = fields.FirstOrDefault(f => f.Name.Equals(fStr, StringComparison.OrdinalIgnoreCase) || $"fid_{f.Fid}".Equals(fStr, StringComparison.OrdinalIgnoreCase));
                                        exportFields.Add(matched != null ? (!string.IsNullOrWhiteSpace(matched.Label) ? matched.Label : matched.Name) : fStr);
                                    }
                                }
                            }
                        }
                    }
                    catch {}
                }

                // Input Event payload structure
                friendlyInput["app"] = _appName;
                friendlyInput["table"] = tableName;
                friendlyInput["on_add_record"] = triggerOnAdded;
                friendlyInput["on_modify_record"] = triggerOnModified;
                friendlyInput["on_delete_record"] = triggerOnDeleted;
                friendlyInput["export_fields"] = exportFields;

                var change = new Dictionary<string, object?>();
                if (eventTypeStr == "Added")
                {
                    change["current"] = FormatChangeState(AsDictionary(inputDict.TryGetValue("NewValues", out var nv) ? nv : null), fields);
                    change["previous"] = null;
                }
                else if (eventTypeStr == "Modified")
                {
                    change["current"] = FormatChangeState(AsDictionary(inputDict.TryGetValue("NewValues", out var nv) ? nv : null), fields);
                    change["previous"] = FormatChangeState(AsDictionary(inputDict.TryGetValue("OldValues", out var ov) ? ov : null), fields);
                }
                else if (eventTypeStr == "Deleted")
                {
                    change["current"] = null;
                    change["previous"] = FormatChangeState(AsDictionary(inputDict.TryGetValue("OldValues", out var ov) ? ov : null), fields);
                }
                friendlyInput["change"] = change;

                // Output (Record details resolved as key-value friendly names)
                var valuesSource = eventTypeStr == "Deleted" 
                    ? AsDictionary(inputDict.TryGetValue("OldValues", out var ov2) ? ov2 : null) 
                    : AsDictionary(inputDict.TryGetValue("NewValues", out var nv2) ? nv2 : null);

                var outputRecord = MapFieldValuesToUserFriendly(fields, valuesSource);
                foreach (var kvp in outputRecord)
                {
                    friendlyOutput[kvp.Key] = kvp.Value;
                }

                // Metadata Section
                var metadataContext = new Dictionary<string, object?>();
                metadataContext["app_name"] = _appName;
                metadataContext["app_id"] = _pipelinePublicId.ToString();
                metadataContext["table_name"] = tableName;
                metadataContext["table_id"] = tableGuidStr;
                metadata["context"] = metadataContext;

                var timestampStr = inputDict.TryGetValue("EventTimestamp", out var ts) ? ts?.ToString() : null;
                DateTime triggerTime = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(timestampStr) && DateTime.TryParse(timestampStr, out var triggerTimeVal))
                {
                    triggerTime = triggerTimeVal;
                }
                metadata["occurred_at"] = new Dictionary<string, object?>
                {
                    { "@type", "datetime" },
                    { "time", new DateTimeOffset(triggerTime.ToUniversalTime()).ToUnixTimeMilliseconds() },
                    { "iso", triggerTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") }
                };

                metadata["user"] = new Dictionary<string, object?>
                {
                    { "displayName", _triggeredByUserDisplayName }
                };

                metadata["record"] = new Dictionary<string, object?>
                {
                    { "action", eventTypeStr }
                };

                var structList = new List<Dictionary<string, object?>>();
                foreach (var f in fields)
                {
                    structList.Add(new Dictionary<string, object?>
                    {
                        { "name", !string.IsNullOrWhiteSpace(f.Label) ? f.Label : f.Name },
                        { "field_id", f.Fid ?? f.Id },
                        { "field_type", f.TypeCode }
                    });
                }
                metadata["struct"] = structList;

                // Technical Trace Details
                technicalDetails["MessageId"] = inputDict.TryGetValue("MessageId", out var msgId) ? msgId : null;
                technicalDetails["BatchId"] = inputDict.TryGetValue("BatchId", out var bId) ? bId : null;
                technicalDetails["PipelineId"] = inputDict.TryGetValue("PipelineId", out var pId) ? pId : null;
                technicalDetails["ConnectionName"] = connectionName;
                technicalDetails["ConnectionPublicId"] = connectionGuidStr;
                technicalDetails["AppPublicId"] = inputDict.TryGetValue("AppPublicId", out var appPubId) ? appPubId : null;
                technicalDetails["TablePublicId"] = tableGuidStr;
                technicalDetails["RecordPublicId"] = recordGuidStr;

                logMessage = $@"Record ""{recordDisplayName}"" was {eventTypeStr?.ToLowerInvariant() ?? "added"} to {tableName} and triggered this pipeline.";
            }
            else if (subtype == "search-records")
            {
                var tableGuidStr = inputDict.TryGetValue("TableId", out var tIdObj) ? tIdObj?.ToString() : null;
                var filterField = inputDict.TryGetValue("FilterField", out var ffObj) ? ffObj?.ToString() : null;
                var filterValue = inputDict.TryGetValue("FilterValue", out var fvObj) ? fvObj?.ToString() : null;
                var maxResults = inputDict.TryGetValue("MaxResults", out var mrObj) ? mrObj?.ToString() : "10";

                string tableName = "Table";
                List<AppField> fields = new();
                if (!string.IsNullOrEmpty(tableGuidStr) && Guid.TryParse(tableGuidStr, out var tGuid))
                {
                    var meta = GetOrFetchTableMetadataAsync(tGuid, ct).GetAwaiter().GetResult();
                    if (meta != null)
                    {
                        tableName = meta.Value.Table.Name;
                        fields = meta.Value.Fields;
                    }
                }

                string friendlyFilterField = filterField ?? string.Empty;
                if (!string.IsNullOrEmpty(filterField))
                {
                    var matchedField = fields.FirstOrDefault(f => 
                        f.Name.Equals(filterField, StringComparison.OrdinalIgnoreCase) || 
                        $"fid_{f.Id}".Equals(filterField, StringComparison.OrdinalIgnoreCase) ||
                        $"fid_{f.Fid}".Equals(filterField, StringComparison.OrdinalIgnoreCase));
                    if (matchedField != null)
                    {
                        friendlyFilterField = !string.IsNullOrWhiteSpace(matchedField.Label) ? matchedField.Label : matchedField.Name;
                    }
                }

                friendlyInput["Table"] = tableName;
                if (!string.IsNullOrEmpty(friendlyFilterField))
                {
                    friendlyInput["Filter"] = $"{friendlyFilterField} Equals \"{filterValue}\"";
                }
                friendlyInput["Max Results"] = maxResults;

                int recordsCount = 0;
                var friendlyRecords = new List<Dictionary<string, object?>>();
                if (outputDict.TryGetValue("records", out var recsObj) && recsObj != null)
                {
                    var recsList = AsList(recsObj);
                    recordsCount = recsList.Count;
                    int limitCount = Math.Min(recordsCount, 5);
                    for (int i = 0; i < limitCount; i++)
                    {
                        friendlyRecords.Add(MapFieldValuesToUserFriendly(fields, AsDictionary(recsList[i])));
                    }
                }

                friendlyOutput["Records Found"] = recordsCount;
                friendlyOutput["Records Preview"] = friendlyRecords;

                technicalDetails["TablePublicId"] = tableGuidStr;
                logMessage = $"Found {recordsCount} records in {tableName} matching criteria.";
            }
            else if (subtype == "create-record")
            {
                var tableGuidStr = inputDict.TryGetValue("TableId", out var tIdObj) ? tIdObj?.ToString() : null;
                string tableName = "Table";
                List<AppField> fields = new();
                Guid tGuid = Guid.Empty;
                if (!string.IsNullOrEmpty(tableGuidStr) && Guid.TryParse(tableGuidStr, out tGuid))
                {
                    var meta = GetOrFetchTableMetadataAsync(tGuid, ct).GetAwaiter().GetResult();
                    if (meta != null)
                    {
                        tableName = meta.Value.Table.Name;
                        fields = meta.Value.Fields;
                    }
                }

                friendlyInput["Table"] = tableName;
                if (inputDict.TryGetValue("FieldMappings", out var fmObj) && fmObj != null)
                {
                    friendlyInput["Fields"] = MapFieldValuesToUserFriendly(fields, AsDictionary(fmObj));
                }

                var createdRecordGuidStr = outputDict.TryGetValue("CreatedRecordPublicId", out var crIdObj) ? crIdObj?.ToString() : null;
                string recordDisplayName = createdRecordGuidStr ?? "New Record";
                if (!string.IsNullOrEmpty(createdRecordGuidStr) && Guid.TryParse(createdRecordGuidStr, out var crGuid) && fields.Count > 0)
                {
                    var meta = GetOrFetchTableMetadataAsync(tGuid, ct).GetAwaiter().GetResult();
                    if (meta != null)
                    {
                        recordDisplayName = GetOrFetchRecordDisplayAsync(meta.Value.Table, fields, crGuid, ct).GetAwaiter().GetResult();
                    }
                }

                friendlyOutput["Record"] = recordDisplayName;
                friendlyOutput["CreatedRecordPublicId"] = createdRecordGuidStr;
                friendlyOutput["Status"] = "Created";

                technicalDetails["TablePublicId"] = tableGuidStr;
                technicalDetails["CreatedRecordPublicId"] = createdRecordGuidStr;

                logMessage = $@"Created record ""{recordDisplayName}"" in {tableName}.";
            }
            else if (subtype == "update-record")
            {
                var tableGuidStr = inputDict.TryGetValue("TableId", out var tIdObj) ? tIdObj?.ToString() : null;
                var targetRecordGuidStr = inputDict.TryGetValue("TargetRecordId", out var trIdObj) ? trIdObj?.ToString() : null;

                string tableName = "Table";
                string recordDisplayName = targetRecordGuidStr ?? "Record";
                List<AppField> fields = new();

                if (!string.IsNullOrEmpty(tableGuidStr) && Guid.TryParse(tableGuidStr, out var tGuid))
                {
                    var meta = GetOrFetchTableMetadataAsync(tGuid, ct).GetAwaiter().GetResult();
                    if (meta != null)
                    {
                        tableName = meta.Value.Table.Name;
                        fields = meta.Value.Fields;

                        if (!string.IsNullOrEmpty(targetRecordGuidStr) && Guid.TryParse(targetRecordGuidStr, out var trGuid))
                        {
                            recordDisplayName = GetOrFetchRecordDisplayAsync(meta.Value.Table, fields, trGuid, ct).GetAwaiter().GetResult();
                        }
                    }
                }

                friendlyInput["Table"] = tableName;
                friendlyInput["Record"] = recordDisplayName;
                if (inputDict.TryGetValue("FieldMappings", out var fmObj) && fmObj != null)
                {
                    friendlyInput["Fields"] = MapFieldValuesToUserFriendly(fields, AsDictionary(fmObj));
                }

                friendlyOutput["Record"] = recordDisplayName;
                friendlyOutput["UpdatedRecordPublicId"] = targetRecordGuidStr;
                friendlyOutput["Status"] = "Updated";

                technicalDetails["TablePublicId"] = tableGuidStr;
                technicalDetails["TargetRecordPublicId"] = targetRecordGuidStr;

                logMessage = $@"Updated record ""{recordDisplayName}"" in {tableName}.";
            }
            else if (subtype == "delete-record")
            {
                var tableGuidStr = inputDict.TryGetValue("TableId", out var tIdObj) ? tIdObj?.ToString() : null;
                var targetRecordGuidStr = inputDict.TryGetValue("TargetRecordId", out var trIdObj) ? trIdObj?.ToString() : null;

                string tableName = "Table";
                string recordDisplayName = targetRecordGuidStr ?? "Record";
                
                if (!string.IsNullOrEmpty(tableGuidStr) && Guid.TryParse(tableGuidStr, out var tGuid))
                {
                    var meta = GetOrFetchTableMetadataAsync(tGuid, ct).GetAwaiter().GetResult();
                    if (meta != null)
                    {
                        tableName = meta.Value.Table.Name;
                        if (!string.IsNullOrEmpty(targetRecordGuidStr) && Guid.TryParse(targetRecordGuidStr, out var trGuid))
                        {
                            if (_recordDisplayCache.TryGetValue(trGuid, out var cachedName))
                            {
                                recordDisplayName = cachedName;
                            }
                        }
                    }
                }

                friendlyInput["Table"] = tableName;
                friendlyInput["Record"] = recordDisplayName;

                friendlyOutput["Status"] = "Deleted";

                technicalDetails["TablePublicId"] = tableGuidStr;
                technicalDetails["TargetRecordPublicId"] = targetRecordGuidStr;

                logMessage = $@"Deleted record ""{recordDisplayName}"" from {tableName}.";
            }
            else if (subtype == "condition")
            {
                if (inputDict.TryGetValue("RuleGroups", out var rgObj) && rgObj != null)
                {
                    friendlyInput["Criteria"] = AsDictionary(rgObj);
                }
                else
                {
                    var left = inputDict.TryGetValue("LeftOperand", out var l) ? l?.ToString() : string.Empty;
                    var right = inputDict.TryGetValue("RightOperand", out var r) ? r?.ToString() : string.Empty;
                    var op = inputDict.TryGetValue("Operator", out var o) ? o?.ToString() : "equals";
                    friendlyInput["Left"] = left;
                    friendlyInput["Operator"] = op;
                    friendlyInput["Right"] = right;
                }

                var matched = false;
                if (outputDict.TryGetValue("Matched", out var mObj) && mObj != null)
                {
                    if (mObj is bool b) matched = b;
                    else if (bool.TryParse(mObj.ToString(), out var bVal)) matched = bVal;
                }
                var branch = outputDict.TryGetValue("EvaluatedBranch", out var bObj) ? bObj?.ToString() : (matched ? "children" : "elsechildren");
                var branchLabel = branch == "children" ? "Yes" : "No";

                friendlyOutput["Matched"] = matched;
                friendlyOutput["Executed Branch"] = branchLabel;

                logMessage = matched 
                    ? "Condition matched. Executed the Yes branch." 
                    : "Condition failed. Executed the No branch.";
            }
            else if (subtype == "loop")
            {
                var loopOverStepId = inputDict.TryGetValue("LoopOverStepId", out var losId) ? losId?.ToString() : string.Empty;
                var itemCount = inputDict.TryGetValue("ItemCount", out var icObj) ? icObj?.ToString() : "0";

                string sourceStepLabel = loopOverStepId;
                if (long.TryParse(loopOverStepId, out var sId) && _stepLabelCache.TryGetValue(sId, out var sLabel))
                {
                    sourceStepLabel = sLabel;
                }

                friendlyInput["Loop Over"] = sourceStepLabel;
                friendlyInput["Items Count"] = int.TryParse(itemCount, out var ic) ? ic : 0;

                var iterationCount = outputDict.TryGetValue("IterationCount", out var itcObj) ? itcObj?.ToString() : itemCount;
                friendlyOutput["Iterations"] = int.TryParse(iterationCount, out var itc) ? itc : 0;
                friendlyOutput["Status"] = "Completed";

                logMessage = $"Loop completed successfully for {iterationCount} items.";
            }
            else if (subtype == "stop")
            {
                var reason = inputDict.TryGetValue("Reason", out var rObj) ? rObj?.ToString() : "Execution halted by pipeline stop action.";
                friendlyInput["Reason"] = reason;
                friendlyOutput["Status"] = "Stopped";
                friendlyOutput["Reason"] = reason;

                logMessage = $"Execution halted: {reason}";
            }
            else if (subtype == "send-email" || subtype == "send-email-outlook")
            {
                var to = inputDict.TryGetValue("To", out var tVal) ? tVal?.ToString() : string.Empty;
                var subject = inputDict.TryGetValue("Subject", out var sVal) ? sVal?.ToString() : string.Empty;
                var body = inputDict.TryGetValue("Body", out var bVal) ? bVal?.ToString() : string.Empty;

                friendlyInput["To"] = to;
                friendlyInput["Subject"] = subject;
                friendlyInput["Body"] = body.Length > 200 ? body.Substring(0, 200) + "... [TRUNCATED]" : body;

                friendlyOutput["Status"] = "Sent";
                
                logMessage = $"Sent email to {to} with subject \"{subject}\".";
            }
            else if (subtype == "make-request")
            {
                var url = inputDict.TryGetValue("Url", out var urlObj) ? urlObj?.ToString() : string.Empty;
                var method = inputDict.TryGetValue("Method", out var mVal) ? mVal?.ToString() : "GET";
                
                string domain = string.Empty;
                try
                {
                    var uri = new Uri(url);
                    domain = uri.Host;
                }
                catch
                {
                    domain = url;
                }

                friendlyInput["URL"] = url;
                friendlyInput["Method"] = method;

                if (inputDict.TryGetValue("Headers", out var hObj) && hObj != null)
                {
                    friendlyInput["Headers"] = AsDictionary(hObj);
                }

                if (inputDict.TryGetValue("Body", out var bdyObj) && bdyObj != null)
                {
                    var bdyStr = bdyObj.ToString() ?? string.Empty;
                    friendlyInput["Body"] = bdyStr.Length > 200 ? bdyStr.Substring(0, 200) + "... [TRUNCATED]" : bdyStr;
                }

                int httpStatusCode = 200;
                if (outputDict.TryGetValue("HTTPStatus", out var hsObj) && hsObj != null && int.TryParse(hsObj.ToString(), out var hsVal))
                {
                    httpStatusCode = hsVal;
                }
                else if (outputDict.TryGetValue("HttpStatusCode", out var hscObj) && hscObj != null && int.TryParse(hscObj.ToString(), out var hscVal))
                {
                    httpStatusCode = hscVal;
                }

                long responseSize = 0;
                if (rawOutputJson != null)
                {
                    responseSize = rawOutputJson.Length;
                }

                friendlyOutput["HTTP Status"] = httpStatusCode;
                friendlyOutput["Response Size"] = responseSize;

                logMessage = $"{method} request to {domain} completed with HTTP {httpStatusCode}.";
            }
            else if (subtype == "prepare-bulk-upsert")
            {
                var tableGuidStr = inputDict.TryGetValue("TableLabel", out var tlObj) ? tlObj?.ToString() : null;
                var mergeKeyFid = inputDict.TryGetValue("MergeKeyFid", out var mkObj) ? mkObj?.ToString() : null;

                string tableName = "Table";
                List<AppField> fields = new();
                if (!string.IsNullOrEmpty(tableGuidStr) && Guid.TryParse(tableGuidStr, out var tGuid))
                {
                    var meta = GetOrFetchTableMetadataAsync(tGuid, ct).GetAwaiter().GetResult();
                    if (meta != null)
                    {
                        tableName = meta.Value.Table.Name;
                        fields = meta.Value.Fields;
                    }
                }

                string friendlyMergeKey = mergeKeyFid ?? string.Empty;
                if (!string.IsNullOrEmpty(mergeKeyFid))
                {
                    var matchedField = fields.FirstOrDefault(f => 
                        f.Name.Equals(mergeKeyFid, StringComparison.OrdinalIgnoreCase) || 
                        $"fid_{f.Id}".Equals(mergeKeyFid, StringComparison.OrdinalIgnoreCase) ||
                        $"fid_{f.Fid}".Equals(mergeKeyFid, StringComparison.OrdinalIgnoreCase));
                    if (matchedField != null)
                    {
                        friendlyMergeKey = !string.IsNullOrWhiteSpace(matchedField.Label) ? matchedField.Label : matchedField.Name;
                    }
                }

                friendlyInput["Table"] = tableName;
                friendlyInput["Merge Key"] = friendlyMergeKey;

                friendlyOutput["Session ID"] = technicalDetails["StepRefId"];
                friendlyOutput["Status"] = "Prepared";

                logMessage = $"Prepared bulk upsert session for {tableName} merging on {friendlyMergeKey}.";
            }
            else if (subtype == "add-bulk-upsert-row")
            {
                var parentRefId = inputDict.TryGetValue("ParentUpsertStepRefId", out var pRef) ? pRef?.ToString() : string.Empty;
                
                friendlyInput["Parent Bulk Session ID"] = parentRefId;
                if (inputDict.TryGetValue("FieldMappings", out var fmObj) && fmObj != null)
                {
                    friendlyInput["Fields"] = MapFieldValuesToUserFriendly(new List<AppField>(), AsDictionary(fmObj));
                }

                var rowCount = outputDict.TryGetValue("RowCount", out var rcObj) ? rcObj?.ToString() : "1";
                friendlyOutput["Total Enqueued Rows"] = int.TryParse(rowCount, out var rc) ? rc : 1;
                friendlyOutput["Status"] = "Row Added";

                logMessage = $"Added row to bulk upsert session. Enqueued total of {rowCount} rows.";
            }
            else if (subtype == "commit-upsert")
            {
                var parentRefId = inputDict.TryGetValue("ParentUpsertStepRefId", out var pRef) ? pRef?.ToString() : string.Empty;
                friendlyInput["Parent Bulk Session ID"] = parentRefId;

                var inserted = outputDict.TryGetValue("InsertedCount", out var insObj) ? insObj?.ToString() : "0";
                var updated = outputDict.TryGetValue("UpdatedCount", out var updObj) ? updObj?.ToString() : "0";

                friendlyOutput["Inserted Record Count"] = int.TryParse(inserted, out var ins) ? ins : 0;
                friendlyOutput["Updated Record Count"] = int.TryParse(updated, out var upd) ? upd : 0;
                friendlyOutput["Status"] = "Committed";

                logMessage = $"Committed bulk upsert. Inserted {inserted} records and updated {updated} records.";
            }
            else if (subtype == "upload-file")
            {
                var fileUrl = inputDict.TryGetValue("FileUrl", out var fUrl) ? fUrl?.ToString() : string.Empty;
                var fileName = inputDict.TryGetValue("FileName", out var fName) ? fName?.ToString() : string.Empty;

                friendlyInput["File Source URL"] = fileUrl;
                friendlyInput["File Name"] = fileName;

                var uploadedFileName = outputDict.TryGetValue("Name", out var unObj) ? unObj?.ToString() : fileName;
                var size = outputDict.TryGetValue("Size", out var szObj) ? szObj?.ToString() : "0";
                var cType = outputDict.TryGetValue("ContentType", out var ctObj) ? ctObj?.ToString() : "application/octet-stream";

                friendlyOutput["Uploaded File Name"] = uploadedFileName;
                friendlyOutput["File Size"] = long.TryParse(size, out var sz) ? sz : 0;
                friendlyOutput["Content Type"] = cType;

                logMessage = $"Uploaded file \"{uploadedFileName}\" successfully ({size} bytes).";
            }
            else
            {
                friendlyInput["Raw Input"] = inputDict;
                friendlyOutput["Raw Output"] = outputDict;
                logMessage = $"Step executed: Type: {step.Type}, Subtype: {step.Subtype}";
            }
        }
        catch (Exception ex)
        {
            friendlyInput["Error"] = "Failed to format input context.";
            friendlyOutput["Error"] = ex.Message;
            logMessage = $"Step execution log failed: {ex.Message}";
        }

        // Sensitive recursively redacted payload build
        var auditInput = new Dictionary<string, object?>();
        foreach (var kvp in friendlyInput)
        {
            auditInput[kvp.Key] = SanitizeObject(kvp.Value);
        }

        var auditOutput = new Dictionary<string, object?>();
        foreach (var kvp in friendlyOutput)
        {
            auditOutput[kvp.Key] = SanitizeObject(kvp.Value);
        }

        // Technical details extraction
        var traceDetails = new Dictionary<string, object?>();
        foreach (var kvp in technicalDetails)
        {
            traceDetails[kvp.Key] = SanitizeObject(kvp.Value);
        }

        // Combined UI DTO formats inside database Context Json columns
        var finalInputContext = new Dictionary<string, object?>
        {
            { "Header", header },
            { "Input", auditInput },
            { "Metadata", metadata },
            { "TechnicalDetails", traceDetails }
        };

        var finalOutputContext = new Dictionary<string, object?>
        {
            { "Output", auditOutput },
            { "TechnicalDetails", traceDetails }
        };

        var inputJson = SerializeAndTruncate(finalInputContext);
        var outputJson = SerializeAndTruncate(finalOutputContext);

        return (inputJson, outputJson, logMessage);
    }

    private object? FormatChangeState(Dictionary<string, object?>? valuesSource, List<AppField> fields)
    {
        if (valuesSource == null || valuesSource.Count == 0) return null;

        var fieldsList = new List<Dictionary<string, object?>>();
        foreach (var kvp in valuesSource)
        {
            var key = kvp.Key;
            AppField? matchedField = null;
            if (key.StartsWith("fid_", StringComparison.OrdinalIgnoreCase) && int.TryParse(key.Substring(4), out var num))
            {
                matchedField = fields.FirstOrDefault(f => f.Fid == num || f.Id == num);
            }
            
            if (matchedField == null)
            {
                matchedField = fields.FirstOrDefault(f => f.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || (f.Label != null && f.Label.Equals(key, StringComparison.OrdinalIgnoreCase)));
            }

            var fieldId = matchedField?.Fid ?? matchedField?.Id ?? 0;
            var fieldName = matchedField != null ? (!string.IsNullOrWhiteSpace(matchedField.Label) ? matchedField.Label : matchedField.Name) : key;
            var rawVal = kvp.Value;
            
            object? finalVal = rawVal;
            if (rawVal != null && matchedField != null && (matchedField.TypeCode.Equals("DATETIME", StringComparison.OrdinalIgnoreCase) || matchedField.TypeCode.Equals("DATE", StringComparison.OrdinalIgnoreCase)))
            {
                if (DateTime.TryParse(rawVal.ToString(), out var dt))
                {
                    var utcDt = dt.ToUniversalTime();
                    finalVal = new Dictionary<string, object?>
                    {
                        { "@type", "datetime" },
                        { "time", new DateTimeOffset(utcDt).ToUnixTimeMilliseconds() },
                        { "iso", utcDt.ToString("yyyy-MM-ddTHH:mm:ssZ") }
                    };
                }
            }

            var fieldRep = new Dictionary<string, object?>
            {
                { "id", fieldId },
                { "name", fieldName },
                { "value", finalVal }
            };

            if (matchedField != null && rawVal != null)
            {
                var printVal = FormatPrintValue(rawVal, matchedField);
                if (printVal != null)
                {
                    fieldRep["printValue"] = printVal;
                }
            }

            fieldsList.Add(fieldRep);
        }

        return new Dictionary<string, object?>
        {
            { "fields", fieldsList }
        };
    }

    private static string? FormatPrintValue(object value, AppField field)
    {
        if (value == null) return null;
        var type = field.TypeCode?.ToUpperInvariant();
        if (type == "CURRENCY")
        {
            if (decimal.TryParse(value.ToString(), out var dec))
            {
                string symbol = "$";
                if (!string.IsNullOrWhiteSpace(field.Settings))
                {
                    try
                    {
                        using var settingsDoc = JsonDocument.Parse(field.Settings);
                        if (settingsDoc.RootElement.TryGetProperty("currencySymbol", out var symProp))
                        {
                            symbol = symProp.GetString() ?? "$";
                        }
                    }
                    catch {}
                }
                return $"{symbol}{dec:N2}";
            }
        }
        else if (type == "PERCENT")
        {
            if (decimal.TryParse(value.ToString(), out var dec))
            {
                return $"{dec * 100:N2}%";
            }
        }
        else if (type == "DATETIME" || type == "DATE")
        {
            if (DateTime.TryParse(value.ToString(), out var dt))
            {
                return dt.ToString("g");
            }
        }
        return null;
    }

    private Dictionary<string, object?> MapFieldValuesToUserFriendly(
        List<AppField> fields, 
        Dictionary<string, object?> rawValues)
    {
        var friendly = new Dictionary<string, object?>();
        var labelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            var displayName = !string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name;
            if (!labelCounts.ContainsKey(displayName))
                labelCounts[displayName] = 0;
            labelCounts[displayName]++;
        }

        foreach (var kvp in rawValues)
        {
            var key = kvp.Key;
            AppField? matchedField = null;
            
            if (key.StartsWith("fid_", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = key.Substring(4);
                if (int.TryParse(suffix, out var num))
                {
                    matchedField = fields.FirstOrDefault(f => f.Fid == num || f.Id == num);
                }
            }
            
            if (matchedField == null)
            {
                matchedField = fields.FirstOrDefault(f => 
                    f.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || 
                    (f.Label != null && f.Label.Equals(key, StringComparison.OrdinalIgnoreCase)));
            }

            if (matchedField != null)
            {
                var displayName = !string.IsNullOrWhiteSpace(matchedField.Label) ? matchedField.Label : matchedField.Name;
                if (labelCounts.TryGetValue(displayName, out var count) && count > 1)
                {
                    int occurrence = 1;
                    var idx = fields.IndexOf(matchedField);
                    var precedingMatchCount = fields.Take(idx).Count(f => 
                        (!string.IsNullOrWhiteSpace(f.Label) ? f.Label : f.Name).Equals(displayName, StringComparison.OrdinalIgnoreCase));
                    occurrence = precedingMatchCount + 1;
                    displayName = $"{displayName} ({occurrence})";
                }

                object? val = kvp.Value;
                if (val != null && (matchedField.TypeCode.Equals("DATETIME", StringComparison.OrdinalIgnoreCase) || matchedField.TypeCode.Equals("DATE", StringComparison.OrdinalIgnoreCase)))
                {
                    if (DateTime.TryParse(val.ToString(), out var dt))
                    {
                        var utcDt = dt.ToUniversalTime();
                        val = new Dictionary<string, object?>
                        {
                            { "@type", "datetime" },
                            { "time", new DateTimeOffset(utcDt).ToUnixTimeMilliseconds() },
                            { "iso", utcDt.ToString("yyyy-MM-ddTHH:mm:ssZ") }
                        };
                    }
                }

                friendly[displayName] = val;
            }
            else
            {
                friendly[key] = kvp.Value;
            }
        }
        return friendly;
    }

    private string GetRecordDisplayValue(
        AppTable table, 
        List<AppField> fields, 
        Dictionary<string, object?> fieldValues, 
        string recordPublicId)
    {
        if (table.DisplayFieldId.HasValue)
        {
            var displayField = fields.FirstOrDefault(f => f.Id == table.DisplayFieldId.Value);
            if (displayField != null && displayField.Fid.HasValue)
            {
                var fidKey = $"fid_{displayField.Fid.Value}";
                if (fieldValues.TryGetValue(fidKey, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()))
                {
                    return val.ToString()!;
                }
            }
        }
        
        var fallbackField = fields.FirstOrDefault(f => 
            f.Name.Equals("Name", StringComparison.OrdinalIgnoreCase) || 
            f.Name.Equals("Title", StringComparison.OrdinalIgnoreCase) || 
            (f.Label != null && (f.Label.Equals("Name", StringComparison.OrdinalIgnoreCase) || f.Label.Equals("Title", StringComparison.OrdinalIgnoreCase))));
            
        if (fallbackField != null && fallbackField.Fid.HasValue)
        {
            var fidKey = $"fid_{fallbackField.Fid.Value}";
            if (fieldValues.TryGetValue(fidKey, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()))
            {
                return val.ToString()!;
            }
        }
        
        foreach (var field in fields)
        {
            if (field.Fid.HasValue && field.TypeCode.ToUpperInvariant() == "TEXT")
            {
                var fidKey = $"fid_{field.Fid.Value}";
                if (fieldValues.TryGetValue(fidKey, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()))
                {
                    return val.ToString()!;
                }
            }
        }

        return recordPublicId;
    }

    private Dictionary<string, object?> DeserializeJsonToDict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(json);
            return DeserializeJsonElementToDict(doc.RootElement);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private Dictionary<string, object?> DeserializeJsonElementToDict(JsonElement el)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                dict[prop.Name] = ConvertJsonElement(prop.Value);
            }
        }
        return dict;
    }

    private object? ConvertJsonElement(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => DeserializeJsonElementToDict(el),
            JsonValueKind.Array => DeserializeJsonElementToList(el),
            _ => el.GetRawText()
        };
    }

    private List<object?> DeserializeJsonElementToList(JsonElement el)
    {
        var list = new List<object?>();
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                list.Add(ConvertJsonElement(item));
            }
        }
        return list;
    }

    private Dictionary<string, object?> AsDictionary(object? obj)
    {
        if (obj == null) return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (obj is Dictionary<string, object?> d) return d;
        if (obj is IDictionary<string, object?> id) return new Dictionary<string, object?>(id, StringComparer.OrdinalIgnoreCase);
        if (obj is JsonElement je && je.ValueKind == JsonValueKind.Object) return DeserializeJsonElementToDict(je);
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private List<object?> AsList(object? obj)
    {
        if (obj == null) return new List<object?>();
        if (obj is List<object?> l) return l;
        if (obj is IEnumerable ie)
        {
            var res = new List<object?>();
            foreach (var item in ie) res.Add(item);
            return res;
        }
        if (obj is JsonElement je && je.ValueKind == JsonValueKind.Array) return DeserializeJsonElementToList(je);
        return new List<object?>();
    }

    private static object? SanitizeObject(object? obj)
    {
        if (obj == null) return null;

        if (obj is string str)
        {
            return str;
        }

        if (obj is IDictionary<string, object?> dictStr)
        {
            var newDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in dictStr)
            {
                if (IsSensitiveKey(kvp.Key))
                {
                    newDict[kvp.Key] = "[REDACTED]";
                }
                else
                {
                    newDict[kvp.Key] = SanitizeObject(kvp.Value);
                }
            }
            return newDict;
        }

        if (obj is IDictionary<long, object?> dictLong)
        {
            var newDict = new Dictionary<long, object?>();
            foreach (var kvp in dictLong)
            {
                newDict[kvp.Key] = SanitizeObject(kvp.Value);
            }
            return newDict;
        }

        if (obj is JsonElement je)
        {
            return SanitizeJsonElement(je);
        }

        if (obj is IEnumerable enumerable)
        {
            var newList = new List<object?>();
            foreach (var item in enumerable)
            {
                newList.Add(SanitizeObject(item));
            }
            return newList;
        }

        var type = obj.GetType();
        if (type.IsClass)
        {
            var newDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in type.GetProperties())
            {
                if (!prop.CanRead) continue;
                try
                {
                    var val = prop.GetValue(obj);
                    if (IsSensitiveKey(prop.Name))
                    {
                        newDict[prop.Name] = "[REDACTED]";
                    }
                    else
                    {
                        newDict[prop.Name] = SanitizeObject(val);
                    }
                }
                catch {}
            }
            return newDict;
        }

        return obj;
    }

    private static object? SanitizeJsonElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in el.EnumerateObject())
                {
                    if (IsSensitiveKey(prop.Name))
                    {
                        dict[prop.Name] = "[REDACTED]";
                    }
                    else
                    {
                        dict[prop.Name] = SanitizeJsonElement(prop.Value);
                    }
                }
                return dict;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray())
                {
                    list.Add(SanitizeJsonElement(item));
                }
                return list;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l;
                return el.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                return el.GetRawText();
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        var sensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "authorization", "proxy-authorization", "cookie", "set-cookie", 
            "password", "passwd", "token", "access_token", "refresh_token", 
            "api-key", "api_key", "x-api-key", "client-secret", "client_secret", "secret"
        };
        return sensitiveKeys.Any(sk => key.Contains(sk, StringComparison.OrdinalIgnoreCase));
    }

    private string SerializeAndTruncate(object obj, int maxChars = 32000)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj);
            if (json.Length <= maxChars)
            {
                return json;
            }

            var truncatedObj = TruncateLargeProperties(obj);
            json = JsonSerializer.Serialize(truncatedObj);
            if (json.Length <= maxChars)
            {
                return json;
            }

            return JsonSerializer.Serialize(new
            {
                Truncated = true,
                OriginalLength = json.Length,
                Message = "Payload too large to log fully."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { Error = "Failed to serialize audit context.", Details = ex.Message });
        }
    }

    private static object? TruncateLargeProperties(object? obj)
    {
        if (obj == null) return null;

        if (obj is string str)
        {
            if (str.Length > 1000)
            {
                return str.Substring(0, 1000) + "... [TRUNCATED]";
            }
            return str;
        }

        if (obj is IDictionary<string, object?> dictStr)
        {
            var newDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in dictStr)
            {
                newDict[kvp.Key] = TruncateLargeProperties(kvp.Value);
            }
            return newDict;
        }

        if (obj is IDictionary<long, object?> dictLong)
        {
            var newDict = new Dictionary<long, object?>();
            foreach (var kvp in dictLong)
            {
                newDict[kvp.Key] = TruncateLargeProperties(kvp.Value);
            }
            return newDict;
        }

        if (obj is IEnumerable enumerable)
        {
            var list = new List<object?>();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count > 20)
                {
                    list.Add("... [ADDITIONAL ITEMS TRUNCATED]");
                    break;
                }
                list.Add(TruncateLargeProperties(item));
                count++;
            }
            return list;
        }

        return obj;
    }

    private class NewEventStepConfig
    {
        public bool TriggerOnAdded { get; set; }
        public bool TriggerOnModified { get; set; }
        public bool TriggerOnDeleted { get; set; }
        public List<string>? SubsequentFields { get; set; }
    }
}
