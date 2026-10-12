namespace PowerBase.Application.Imports;

/// <summary>Which of an import's tables read a workbook sheet by sheet, and which sheets that takes.</summary>
public static class ImportSheetModes
{
    /// <summary>Every sheet any table maps separately, once each, in the order first met.</summary>
    public static IReadOnlyList<string> SheetsUsed(ImportDefinitionConfig cfg) =>
        cfg.SheetMappings.Select(s => s.Sheet).Concat(cfg.AdditionalTargets.SelectMany(t => t.SheetMappings.Select(s => s.Sheet)))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when some table does not read sheet by sheet: it reads the file as one source, as an import always did.</summary>
    public static bool AnyTableReadsTheWholeFile(ImportDefinitionConfig cfg) =>
        cfg.SheetMappings.Count == 0 || cfg.AdditionalTargets.Any(t => t.SheetMappings.Count == 0);
}
