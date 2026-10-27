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
}
