using PowerBase.Application.Import.Reporting;

namespace PowerBase.Application.Import.Commands.ImportAppFromPbl;

/// <summary>An element the PBL document described but that import did not create — e.g. a
/// field whose type isn't supported yet. Always reported, never silently dropped.</summary>
public sealed class ImportSkippedItem
{
    public string LogicalRef { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Per-formula outcome of the formula translation pass — clean, or needs manual
/// review (see <see cref="FormulaTranslation.FormulaTranslationStatus"/>). Reported even for
/// clean formulas so the report gives a complete account of every formula encountered.</summary>
public sealed class FormulaTranslationReportItem
{
    public string LogicalRef { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public List<string> Diagnostics { get; init; } = [];
}

/// <summary>An application of the file that could not be imported (and was removed again if it had been partly built).</summary>
public sealed class ImportAppFailure
{
    public string AppRef { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>Result of importing a file as new apps: one report per application created, plus the ones that failed.</summary>
public sealed class ImportBatchReport
{
    public List<ImportReport> Apps { get; init; } = [];
    public List<ImportAppFailure> Failed { get; init; } = [];
    public int AppsCreated => Apps.Count;
    public int TablesCreated => Apps.Sum(a => a.TablesCreated);
    public int FieldsCreated => Apps.Sum(a => a.FieldsCreated);
    public int ReportsCreated => Apps.Sum(a => a.ReportsCreated);
    public int RelationshipsCreated => Apps.Sum(a => a.RelationshipsCreated);
    public int FormsCreated => Apps.Sum(a => a.FormsCreated);
    public int RolesCreated => Apps.Sum(a => a.RolesCreated);
    public int SkippedCount => Apps.Sum(a => a.Skipped.Count);
    public int PipelinesCreated => Apps.Sum(a => a.PipelinesCreated);
}

public static class PipelineImportStatuses
{
    public const string Created = "Created";
    /// <summary>The PowerFlow exists but its steps could not be saved; they are listed in the issues.</summary>
    public const string CreatedWithoutSteps = "CreatedWithoutSteps";
    public const string NotCreated = "NotCreated";
}

/// <summary>A note about one pipeline or one of its steps. <see cref="StepId"/> is the Quickbase step id (or its location when the
/// step has none); <see cref="IsSkippedStep"/> marks a step that is not in the imported PowerFlow.</summary>
public sealed class PipelineImportIssueItem
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? StepId { get; init; }
    public string? StepName { get; init; }
    public bool IsSkippedStep { get; init; }
}

/// <summary>What became of one Quickbase pipeline. Imported PowerFlows are always off; <see cref="NeedsReview"/> says the
/// PowerFlow does not do everything the source did, or has steps saved as drafts, which keep it from being turned on.</summary>
public sealed class PipelineImportReportItem
{
    public string LogicalRef { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? PipelinePublicId { get; set; }
    public string Status { get; set; } = PipelineImportStatuses.NotCreated;
    public string? SourceOwner { get; init; }
    public int StepsImported { get; set; }
    public int StepsSkipped { get; set; }
    public bool NeedsReview { get; set; }
    /// <summary>True when the import turned the PowerFlow on (<see cref="Mapping.ImportMapping.ActivatePipelines"/>).</summary>
    public bool IsActive { get; set; }
    public List<PipelineImportIssueItem> Issues { get; init; } = [];
}

public sealed class ImportReport
{
    public Guid AppPublicId { get; init; }
    public string AppName { get; init; } = string.Empty;
    public int TablesCreated { get; init; }
    public int FieldsCreated { get; init; }
    public int ReportsCreated { get; init; }
    public int RelationshipsCreated { get; init; }
    public int FormsCreated { get; init; }
    public int FormRulesCreated { get; init; }
    public int RolesCreated { get; init; }
    public int VariablesCreated { get; init; }
    public int PagesCreated { get; init; }
    public List<ImportSkippedItem> Skipped { get; init; } = [];
    public List<FormulaTranslationReportItem> FormulaTranslations { get; init; } = [];
    /// <summary>The file's Quickbase pipelines imported (or not) as PowerFlows of this app. Empty for a file without pipelines.</summary>
    public List<PipelineImportReportItem> Pipelines { get; init; } = [];
    public int PipelinesCreated => Pipelines.Count(p => p.PipelinePublicId.HasValue);

    /// <summary>The same information as <see cref="Skipped"/>/<see cref="FormulaTranslations"/>/<see cref="Pipelines"/>' issues,
    /// restated for the UI: grouped by application/table/field, with severity and a recommended action. Set once the report is
    /// fully assembled (see <see cref="ImportAppFromPblCommandHandler"/>) — empty until then.</summary>
    public ImportIssueReport Issues { get; set; } = new();
}
