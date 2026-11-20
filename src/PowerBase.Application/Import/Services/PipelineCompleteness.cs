using PowerBase.Application.Import.Pbl;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// Strict pipeline import (user rule 2026-11-19): a Quickbase pipeline becomes a PowerFlow only when every one of its steps is
/// imported exactly — no step skipped, no step left as a draft, no setting, input or value dropped. Otherwise the whole pipeline
/// is skipped and the import report lists each step that could not be imported and why.
/// </summary>
public static class PipelineCompleteness
{
    /// <summary>Structural problems: the step itself (its app, table, or the step it depends on) is missing, so the PowerFlow
    /// would not be the same flow. Everything else — a field not found, a Quickbase URL / request to review, a setting or value
    /// dropped — is a warning only: the pipeline is imported and the warning is listed (user rule 2026-11-19).</summary>
    private static readonly HashSet<string> SkipCodes = new(StringComparer.Ordinal)
    {
        "PIPELINE_STEP_TABLE_NOT_MATCHED", "PIPELINE_STEP_UNSUPPORTED", "PIPELINE_STEP_PARENT_SKIPPED",
        "PIPELINE_STEP_DEPENDENCY_SKIPPED", "PIPELINE_NO_VALID_START", "PIPELINE_STEP_TARGET_MISSING",
        "PIPELINE_STEP_TARGET_INVALID", "PIPELINE_STEP_PLACEMENT", "PIPELINE_TABLE_NOT_FOUND", "PIPELINE_APP_NOT_FOUND", "PIPELINE_TABLE_MISSING",
    };

    /// <summary>True when the issue makes the whole pipeline skipped (a step, its app or its table is missing).</summary>
    public static bool IsSkipReason(PblPipelineIssue issue) =>
        issue.IsSkippedStep
        || SkipCodes.Contains(issue.Code)
        // A value that pointed at a step that was not imported (its table was not matched): the flow lost that step.
        || issue.Code == "PIPELINE_REFERENCE_CLEARED" && (issue.Message.Contains(" was not imported", StringComparison.Ordinal)
                                                          || issue.Message.Contains("that step is not imported", StringComparison.Ordinal));

    /// <summary>Null when every step of the pipeline is imported with its app and table; otherwise why it is skipped.</summary>
    public static string? Reason(PblPipeline pipeline)
    {
        if (pipeline.IsUnreadable)
            return "Skipped: the pipeline could not be read.";

        var reasons = pipeline.Issues.Where(IsSkipReason).Select(issue =>
        {
            var step = issue.StepName is { Length: > 0 } || issue.StepId is { Length: > 0 } ? $"Step '{issue.StepName}' ({issue.StepId})" : "Pipeline";
            return $"{step}: {issue.Message} [{issue.Code}]";
        }).Distinct().ToList();
        if (pipeline.SourceStepCount > 0 && pipeline.Steps.Count == 0 && reasons.Count == 0)
            reasons.Add("Pipeline: none of its steps could be imported.");

        return reasons.Count == 0
            ? null
            : $"Skipped: {reasons.Count} problem(s) — a step, its app or its table could not be imported. " + string.Join(" | ", reasons);
    }

    /// <summary>Warnings of an imported pipeline (field not found, URL to review, …): imported, but listed for review.</summary>
    public static IEnumerable<PblPipelineIssue> Warnings(PblPipeline pipeline) =>
        pipeline.Issues.Where(i => !IsSkipReason(i) && (i.ChangesExecution || i.Code.Length > 0));

    private static IEnumerable<PblPipelineStep> Flatten(IEnumerable<PblPipelineStep> steps) =>
        steps.SelectMany(s => new[] { s }.Concat(Flatten(s.Children)).Concat(Flatten(s.ElseChildren))
            .Concat(Flatten(s.SuccessChildren)).Concat(Flatten(s.ErrorChildren)));
}
