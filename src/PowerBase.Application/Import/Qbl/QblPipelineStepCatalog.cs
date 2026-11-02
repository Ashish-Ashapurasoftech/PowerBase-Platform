namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// The Quickbase pipeline steps a PowerFlow can be built from, and the step-picker configuration each one starts from. Only
/// shapes confirmed against a real export are listed (see <c>QBL_COMPATIBILITY_MATRIX.md</c>): a channel step is matched on
/// node type + channel + resource + selector (<c>create</c> is both "Create Record" and "Add a Bulk Upsert Row", told apart by
/// the resource), a control step on its node type. Anything not listed is reported as unsupported, never guessed.
/// </summary>
public static class QblPipelineStepCatalog
{
    public sealed record Entry(string Type, string Subtype);

    public const string QUICKBASE_STEP = "QB::PipelineStep::Quickbase";
    public const string CLOCK_STEP = "QB::PipelineStep::Clock";
    public const string CONDITION_STEP = "QB::PipelineStep::Condition";
    public const string LOOP_STEP = "QB::PipelineStep::Loop";
    public const string STOP_STEP = "QB::PipelineStep::Stop";
    public const string ERROR_HANDLING_STEP = "QB::PipelineStep::ErrorHandling";
    public const string OUTLOOK_STEP = "QB::PipelineStep::MicrosoftOutlook";
    public const string CALLABLE_STEP = "QB::PipelineStep::CloudpipesMeta";
    public const string HTTP_STEP = "QB::PipelineStep::Http";
    public const string WEBHOOKS_STEP = "QB::PipelineStep::Webhooks";

    private static readonly Dictionary<(string NodeType, string Channel, string Resource, string Selector), Entry> ChannelSteps = new()
    {
        [(QUICKBASE_STEP, "quickbase", "record", "on_new_event")] = new("trigger", "new-event"),
        [(QUICKBASE_STEP, "quickbase", "record", "on_new_bulk_event")] = new("trigger", "new-bulk-event"),
        [(QUICKBASE_STEP, "quickbase", "quickbase_api", "make")] = new("action", "make-request"),
        [(HTTP_STEP, "http", "request", "make_request")] = new("action", "make-request"),
        [(WEBHOOKS_STEP, "webhooks", "request", "on_incoming")] = new("trigger", "webhook"),
        [(WEBHOOKS_STEP, "webhooks", "request", "make")] = new("action", "make-request"),
        [(QUICKBASE_STEP, "quickbase", "record", "on_create")] = new("trigger", "new-event"),
        [(QUICKBASE_STEP, "quickbase", "record", "on_update")] = new("trigger", "new-event"),
        [(QUICKBASE_STEP, "quickbase", "record", "search")] = new("query", "search-records"),
        [(QUICKBASE_STEP, "quickbase", "record", "look_up")] = new("query", "look-up-record"),
        [(QUICKBASE_STEP, "quickbase", "record", "create")] = new("action", "create-record"),
        [(QUICKBASE_STEP, "quickbase", "record", "update")] = new("action", "update-record"),
        [(QUICKBASE_STEP, "quickbase", "record", "delete")] = new("action", "delete-record"),
        [(QUICKBASE_STEP, "quickbase", "bulk_record_set", "define")] = new("action", "prepare-bulk-upsert"),
        [(QUICKBASE_STEP, "quickbase", "bulk_record_set", "create")] = new("action", "add-bulk-upsert-row"),
        [(QUICKBASE_STEP, "quickbase", "bulk_record_set", "commit_bulk_upsert")] = new("action", "commit-upsert"),
        [(QUICKBASE_STEP, "quickbase", "bulk_record_set", "copy_records")] = new("action", "copy-records"),
        [(QUICKBASE_STEP, "quickbase", "attachment", "upload")] = new("file", "upload-file"),
        [(QUICKBASE_STEP, "quickbase", "bulk_record_set", "csv_import")] = new("action", "import-with-csv"),
        [(CLOCK_STEP, "clock", "timer", "pause")] = new("action", "pause"),
        [(OUTLOOK_STEP, "microsoft-outlook", "email", "send_email")] = new("email", "send-email-outlook"),
        [(CALLABLE_STEP, "cloudpipes-meta", "pipeline", "on_called")] = new("trigger", "pipeline-called"),
        [(CALLABLE_STEP, "cloudpipes-meta", "pipeline", "call_pipeline")] = new("action", "call-another-pipeline"),
    };

    private static readonly Dictionary<string, Entry> ControlSteps = new(StringComparer.Ordinal)
    {
        [CONDITION_STEP] = new("condition", "if-else"),
        [LOOP_STEP] = new("loop", "for-each"),
        [STOP_STEP] = new("action", "stop"),
        [ERROR_HANDLING_STEP] = new("action", "handle-errors"),
    };

    public static Entry? Find(string nodeType, string? channel, string? resource, string? selector)
    {
        if (ControlSteps.TryGetValue(nodeType, out var control))
            return control;
        if (channel is null || resource is null || selector is null)
            return null;
        return ChannelSteps.TryGetValue((nodeType, channel, resource, selector), out var entry) ? entry : null;
    }

    /// <summary>The configuration the step picker gives a new step of <paramref name="subtype"/> (<c>STEP_TYPES</c> in the
    /// editor's <c>pipeline-step.model.ts</c>), so an imported step opens in the editor exactly like one added by hand.</summary>
    public static Dictionary<string, object?> DefaultConfig(string subtype) => subtype switch
    {
        "new-event" => new(StringComparer.Ordinal)
        {
            ["connection"] = "", ["app"] = "", ["tableLabel"] = "",
            ["triggerOnAdded"] = false, ["triggerOnModified"] = true, ["triggerOnDeleted"] = false,
            ["triggerOnAnyField"] = true, ["subsequentFields"] = new List<object?>(),
            ["triggerOnNumRecordsChanged"] = "any", ["maxRecords"] = null,
        },
        "new-bulk-event" => new(StringComparer.Ordinal)
        {
            ["connection"] = "", ["app"] = "", ["tableLabel"] = "",
            ["triggerOnAdded"] = false, ["triggerOnModified"] = true, ["triggerOnDeleted"] = false,
            ["triggerOnAnyField"] = true, ["subsequentFields"] = new List<object?>(),
            ["triggerOnNumRecordsChanged"] = "any", ["minRecordsChanged"] = null,
        },
        "webhook" => new(StringComparer.Ordinal) { ["authType"] = "no-auth", ["methodType"] = "ANY BELOW", ["jwtAlgorithm"] = "RS256", ["publicKey"] = "" },
        // Make Request has two shapes (the editor's HTTP and PowerBase-account modes); the converter writes the one it needs.
        "make-request" => new(StringComparer.Ordinal),
        "search-records" => new(StringComparer.Ordinal)
        {
            ["tableId"] = "", ["filterField"] = "", ["filterValue"] = "", ["maxResults"] = null, ["compareLocalTime"] = "No",
        },
        "look-up-record" => new(StringComparer.Ordinal)
        {
            ["connection"] = "", ["app"] = "", ["tableLabel"] = "", ["subsequentFields"] = new List<object?>(),
            ["compareLocalTime"] = "No", ["recordIdValue"] = "",
        },
        "create-record" => new(StringComparer.Ordinal) { ["tableId"] = "", ["fieldMappings"] = new List<object?>() },
        "update-record" => new(StringComparer.Ordinal) { ["tableId"] = "", ["targetRecordId"] = "", ["fieldMappings"] = new List<object?>() },
        "delete-record" => new(StringComparer.Ordinal) { ["tableId"] = "", ["targetRecordId"] = "" },
        "prepare-bulk-upsert" => new(StringComparer.Ordinal)
        {
            ["app"] = "", ["tableLabel"] = "", ["fields"] = new List<object?>(), ["mergeField"] = "", ["connection"] = "",
        },
        "add-bulk-upsert-row" => new(StringComparer.Ordinal) { ["bulkRecordSetStepId"] = "", ["rowValues"] = new Dictionary<string, object?>(), ["connection"] = "" },
        "commit-upsert" => new(StringComparer.Ordinal) { ["bulkRecordSetStepId"] = "", ["connection"] = "" },
        "copy-records" => new(StringComparer.Ordinal)
        {
            ["connection"] = "", ["sourceTable"] = "", ["sourceFields"] = new List<object?>(), ["destinationFields"] = new List<object?>(),
            ["advancedQuery"] = "", ["advancedQueryFields"] = new List<object?>(), ["destinationTable"] = "", ["mergeField"] = "",
            ["terminateOnError"] = "Yes",
        },
        "pause" => new(StringComparer.Ordinal) { ["duration"] = 10, ["unit"] = "Seconds" },
        "if-else" => new(StringComparer.Ordinal) { ["leftOperand"] = "", ["operator"] = "equals", ["rightOperand"] = "" },
        "for-each" => new(StringComparer.Ordinal) { ["loopOverStepId"] = "" },
        "stop" => new(StringComparer.Ordinal) { ["reason"] = "Stopped" },
        "handle-errors" => new(StringComparer.Ordinal) { ["fallbackAction"] = "handle" },
        "upload-file" => new(StringComparer.Ordinal)
        {
            ["fileRecordStepId"] = "", ["fileUrl"] = "", ["targetFileField"] = "", ["selectedFileFields"] = new List<object?>(), ["fileName"] = "",
        },
        "import-with-csv" => new(StringComparer.Ordinal)
        {
            ["connection"] = "", ["app"] = "", ["tableLabel"] = "", ["importTemplateId"] = "", ["csvFile"] = "", ["csvUpload"] = "", ["notifyEmails"] = "",
        },
        "send-email-outlook" => new(StringComparer.Ordinal)
        {
            ["permissions"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["signIn"] = true, ["sendMailAsUser"] = true, ["readCalendars"] = true, ["readUserAndSharedMail"] = true, ["readUserMail"] = true,
                ["sendMailOnBehalfOfOthers"] = true, ["fullAccessCalendars"] = true, ["readWriteUserAndSharedMail"] = true,
                ["readUserAndSharedCalendars"] = true, ["readWriteUserAndSharedCalendars"] = true,
            },
            ["sharedMailbox"] = "", ["toAddresses"] = "", ["subject"] = "", ["body"] = "", ["fromAddress"] = "", ["ccAddresses"] = "",
            ["bccAddresses"] = "", ["contentType"] = "HTML", ["importance"] = "Normal", ["saveToSentItems"] = "Yes",
        },
        "pipeline-called" => new(StringComparer.Ordinal) { ["callDefinition"] = "" },
        "call-another-pipeline" => new(StringComparer.Ordinal) { ["callDefinition"] = "", ["arguments"] = new Dictionary<string, object?>(StringComparer.Ordinal) },
        _ => new(StringComparer.Ordinal),
    };
}
