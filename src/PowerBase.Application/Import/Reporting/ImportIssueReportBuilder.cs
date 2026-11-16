using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Queries.ImportPreview;

namespace PowerBase.Application.Import.Reporting;

/// <summary>Turns the importer's existing technical issue lists — <see cref="PblIssue"/>/<see cref="PblIssueDto"/>
/// (preview-time validation/conversion), <see cref="ImportSkippedItem"/>/<see cref="FormulaTranslationReportItem"/>/
/// <see cref="PipelineImportIssueItem"/> (post-import) — into a single structured, groupable
/// <see cref="ImportIssueReport"/>. Nothing here changes what the import does; it only restates what
/// already happened for the UI. No issue is ever dropped — anything that can't be resolved to a
/// table/field still comes through with ElementType "General".</summary>
public static class ImportIssueReportBuilder
{
    public static ImportIssueReport BuildFromPreview(
        string applicationName,
        IEnumerable<PblIssueDto> errors,
        IEnumerable<PblIssueDto> warnings,
        PblDocument? document)
    {
        var resolver = new ImportRefResolver(document);
        var issues = new List<ImportIssue>();

        foreach (var e in errors)
            issues.Add(ToIssue(applicationName, resolver, e.Code, e.Message, e.ElementRef, ImportIssueSeverity.Error, originalConfiguration: null));
        foreach (var w in warnings)
            issues.Add(ToIssue(applicationName, resolver, w.Code, w.Message, w.ElementRef, ImportIssueSeverity.Warning, originalConfiguration: null));

        return Finish(issues);
    }

    public static ImportIssueReport BuildFromImportReport(string applicationName, ImportReport report, PblDocument? document)
    {
        var resolver = new ImportRefResolver(document);
        var issues = new List<ImportIssue>();

        foreach (var s in report.Skipped)
            issues.Add(ToIssue(applicationName, resolver, code: null, s.Reason, s.LogicalRef, ImportIssueSeverity.Warning,
                originalConfiguration: null, fallbackName: s.Name));

        foreach (var f in report.FormulaTranslations.Where(f => f.Status != "Clean"))
        {
            var severity = f.Status == "NeedsManualReview" ? ImportIssueSeverity.Warning : ImportIssueSeverity.Information;
            var message = f.Diagnostics.Count > 0 ? string.Join(" ", f.Diagnostics) : $"Formula '{f.Name}' was adjusted during import.";
            issues.Add(ToIssue(applicationName, resolver, code: $"FORMULA_{f.Status}", message, f.LogicalRef, severity,
                originalConfiguration: null, fallbackName: f.Name));
        }

        foreach (var pipeline in report.Pipelines)
            foreach (var issue in pipeline.Issues)
                issues.Add(ToPipelineIssue(applicationName, pipeline.Name, issue));

        return Finish(issues);
    }

    public static ImportIssueReport BuildFromUpdateSkipped(
        string applicationName,
        IEnumerable<ImportSkippedItem> skipped,
        IEnumerable<FormulaTranslationReportItem> formulaTranslations,
        IEnumerable<PipelineImportReportItem> pipelines,
        IEnumerable<string>? failures,
        PblDocument? document)
    {
        // Same shapes as the create-app report — build a throwaway ImportReport so both paths share one code path.
        var report = new ImportReport
        {
            Skipped = skipped.ToList(),
            FormulaTranslations = formulaTranslations.ToList(),
            Pipelines = pipelines.ToList(),
        };
        var built = BuildFromImportReport(applicationName, report, document);
        if (failures is null) return built;

        // Stage-level failures have no element reference at all — they stay General, but are never dropped.
        var issues = built.Issues.ToList();
        foreach (var failure in failures)
            issues.Add(new ImportIssue
            {
                Severity = ImportIssueSeverity.Error,
                ApplicationName = applicationName,
                ElementType = "General",
                Description = failure,
                Code = string.Empty,
                TechnicalDetail = failure,
            });

        return Finish(issues);
    }

    private static ImportIssue ToPipelineIssue(string applicationName, string pipelineName, PipelineImportIssueItem issue)
    {
        var message = issue.StepName is null ? issue.Message : $"Step '{issue.StepName}' ({issue.StepId}): {issue.Message}";
        var (severity, action) = ImportIssueClassifier.Classify(issue.Code, message, ImportIssueSeverity.Warning);
        return new ImportIssue
        {
            Severity = severity,
            ApplicationName = applicationName,
            TableName = null,
            FieldName = pipelineName,
            ElementType = "Pipeline",
            Description = message,
            RecommendedAction = action,
            Code = issue.Code,
            TechnicalDetail = $"[{issue.Code}] {message}" + (issue.StepId is null ? "" : $" (step {issue.StepId})"),
        };
    }

    private static ImportIssue ToIssue(
        string applicationName,
        ImportRefResolver resolver,
        string? code,
        string message,
        string? elementRef,
        ImportIssueSeverity sourceSeverity,
        string? originalConfiguration,
        string? fallbackName = null)
    {
        var resolved = resolver.Resolve(elementRef);
        var fieldName = resolved?.FieldName ?? fallbackName ?? ImportIssueClassifier.ExtractQuotedName(message);
        var (severity, action) = ImportIssueClassifier.Classify(code, message, sourceSeverity);

        return new ImportIssue
        {
            Severity = severity,
            ApplicationName = applicationName,
            TableName = resolved?.TableName,
            FieldName = fieldName,
            ElementType = resolved?.ElementType ?? "General",
            Description = message,
            OriginalConfiguration = originalConfiguration,
            RecommendedAction = action,
            Code = code ?? string.Empty,
            TechnicalDetail = string.IsNullOrEmpty(code)
                ? $"{message} (ref: {elementRef ?? "none"})"
                : $"[{code}] {message} (ref: {elementRef ?? "none"})",
        };
    }

    private static ImportIssueReport Finish(List<ImportIssue> issues)
    {
        var affectedFields = issues
            .Where(i => i.Severity != ImportIssueSeverity.Information && i.FieldName is not null)
            .Select(i => (i.TableName, i.FieldName))
            .Distinct()
            .Count();

        return new ImportIssueReport
        {
            Issues = issues,
            Summary = new ImportIssueSummary
            {
                ErrorCount = issues.Count(i => i.Severity == ImportIssueSeverity.Error),
                WarningCount = issues.Count(i => i.Severity == ImportIssueSeverity.Warning),
                InformationCount = issues.Count(i => i.Severity == ImportIssueSeverity.Information),
                AffectedFieldCount = affectedFields,
            },
        };
    }
}
