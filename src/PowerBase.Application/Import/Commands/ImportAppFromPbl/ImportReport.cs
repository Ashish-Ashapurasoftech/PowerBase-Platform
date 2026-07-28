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
    public List<ImportSkippedItem> Skipped { get; init; } = [];
    public List<FormulaTranslationReportItem> FormulaTranslations { get; init; } = [];
}
