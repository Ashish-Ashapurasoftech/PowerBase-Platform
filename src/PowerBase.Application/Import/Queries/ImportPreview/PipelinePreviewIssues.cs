using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Reporting;

namespace PowerBase.Application.Import.Queries.ImportPreview;

/// <summary>
/// What the import wizard shows for each PowerFlow of a pipelines-only file: whether it is imported as in Quickbase, imported
/// partially (something must be changed before it is turned on) or not imported, and for every note what the user has to do.
/// </summary>
public static class PipelinePreviewIssues
{
    public const string StatusField = "Import status";

    /// <summary>Notes that need no action: the PowerFlow does what it did in Quickbase.</summary>
    public static readonly HashSet<string> InformationOnlyCodes = new(StringComparer.Ordinal)
    {
        "PIPELINE_TAGS_NOT_IMPORTED", "PIPELINE_HTTP_ACCOUNT_CREATED", "PIPELINE_QUICKBASE_URL_REPLACED", "PIPELINE_REQUEST_URL_MAPPED",
        "PIPELINE_REQUEST_HEADER_DROPPED", "PIPELINE_REQUEST_QB_TABLE_ID_KEPT", "PIPELINE_RENAMED",
    };

    private static readonly Dictionary<string, string> WhatToDo = new(StringComparer.Ordinal)
    {
        ["PIPELINE_REFERENCE_CLEARED"] = "Open the step and enter this value again with a PowerFlows field or formula (the Quickbase value is shown above).",
        ["PIPELINE_FIELD_NOT_FOUND"] = "Add the missing field to the PowerBase table (or import the app again), then choose it in the step.",
        ["PIPELINE_COMPUTED_FIELD_WRITE"] = "The field is calculated in PowerBase. Nothing to do if its formula gives the value; otherwise write to a regular field.",
        ["PIPELINE_EMAIL_PLATFORM_SMTP"] = "Connect the Microsoft Outlook account in the Send an Email step; until then the email is sent by the PowerBase mail server.",
        ["PIPELINE_QUICKBASE_URL_REVIEW"] = "Replace the Quickbase address with the PowerBase page or API address in the step.",
        ["PIPELINE_REQUEST_NEEDS_REVIEW"] = "Open the Make Request step and replace Quickbase ids (saved import, report number) and credentials with PowerBase ones.",
        ["PIPELINE_INPUT_MISSING"] = "Fill in the empty value in the step.",
        ["PIPELINE_INPUT_INVALID"] = "Correct the value in the step.",
        ["PIPELINE_SETTING_NOT_IMPORTED"] = "PowerFlows has no such setting; check the step still does what is needed (for a query, add it as the step's filter).",
        ["PIPELINE_STEP_INCOMPLETE"] = "Complete the step as described above.",
        ["PIPELINE_HTTP_CONNECTION_MISSING"] = "Choose or connect an HTTP account in the Make Request step.",
        ["PIPELINE_TRIGGER_FIELDS_MISSING"] = "Choose the fields the trigger watches.",
        ["PIPELINE_FILTER_NOT_VERIFIED"] = "Set the step's filter.",
        ["PIPELINE_WEBHOOK_NEW_URL"] = "After the import, copy the step's new URL into the outside system that called Quickbase.",
        ["PIPELINE_ADDRESS_PART_INPUT"] = "Set the whole address in the step.",
        ["PIPELINE_TRIGGER_NOT_SAVED"] = "Choose the trigger's app and table.",
        ["PIPELINE_STEP_TABLE_NOT_MATCHED"] = "Choose the step's table.",
    };

    public static bool IsInformation(string? code) => code is not null && InformationOnlyCodes.Contains(code);

    public static string Action(string? code) =>
        code is null ? "Review this item." : IsInformation(code) ? "No action needed." : WhatToDo.TryGetValue(code, out var a) ? a : "Check this step before turning the PowerFlow on.";

    /// <summary>The action for a PowerFlow that is not imported, from its reason.</summary>
    public static string NotImportedAction(string description) =>
        description.Contains("Not found among the imported apps", StringComparison.Ordinal) || description.Contains("Quickbase table id", StringComparison.Ordinal)
            ? "Import the Quickbase application that holds this id first, then import this file again."
            : "PowerFlows has no equivalent for a step of this pipeline; build it in PowerFlows by hand.";

    /// <summary>The status line of one imported PowerFlow, from its notes (null when it has none).</summary>
    public static ImportIssue? Status(string applicationName, string pipelineName, IReadOnlyCollection<ImportIssue> issues, int sourceSteps, int skippedSteps)
    {
        var toChange = issues.Count(i => i.Severity != ImportIssueSeverity.Information);
        if (issues.Count == 0 && skippedSteps == 0) return null;
        var partial = toChange > 0 || skippedSteps > 0;
        var skipped = skippedSteps > 0 ? $" {skippedSteps} of {sourceSteps} Quickbase step(s) were not imported." : "";
        return new ImportIssue
        {
            Severity = partial ? ImportIssueSeverity.Warning : ImportIssueSeverity.Information,
            ApplicationName = applicationName,
            TableName = $"PowerFlow: {pipelineName}",
            FieldName = StatusField,
            ElementType = "Pipeline",
            Code = partial ? "PIPELINE_PARTIAL_IMPORT" : "PIPELINE_IMPORTED_AS_IN_QUICKBASE",
            Description = partial
                ? $"Partial import: the PowerFlow is imported (off), but {toChange} item(s) below must be changed before it is turned on.{skipped}"
                : "Imported as in Quickbase: the notes below need no change.",
            RecommendedAction = partial ? "Make the changes listed below, then turn the PowerFlow on." : "No action needed.",
        };
    }

    /// <summary>True when a step of the pipeline writes a PowerBase API link and the API address is not configured: the import writes
    /// a relative link and reports it (as <c>PblPipelinesImporter</c> does).</summary>
    public static bool UsesSite(PblPipeline pipeline, bool api)
    {
        bool Walk(object? value) => value switch
        {
            PblPipelineSiteValue site => site.Api == api,
            PblPipelineTextValue text => text.Parts.Any(Walk),
            IDictionary<string, object?> map => map.Values.Any(Walk),
            System.Collections.IEnumerable list and not string => list.Cast<object?>().Any(Walk),
            _ => false,
        };
        IEnumerable<PblPipelineStep> All(IEnumerable<PblPipelineStep> steps) =>
            steps.SelectMany(s => new[] { s }.Concat(All(s.Children ?? [])));
        return All(pipeline.Steps).Any(s => Walk(s.Config));
    }
}
