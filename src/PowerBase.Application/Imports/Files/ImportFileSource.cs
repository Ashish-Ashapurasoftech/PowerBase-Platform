using PowerBase.Application.Reports;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports.Files;

public static class ImportSourceKinds
{
    public const string Table = "table";
    public const string File = "file";
    public const string ColumnByName = "name";
    public const string ColumnByPosition = "position";
}

/// <summary>How a file-based import reads its file. Saved with the import, so every run reads each new file the same way.</summary>
public sealed class ImportFileOptions
{
    /// <summary>"csv" or "xlsx".</summary>
    public string Format { get; set; } = ImportFileFormats.Csv;
    public string? Sheet { get; set; }
    /// <summary>The row holding the column names; 0 when the file has none.</summary>
    public int HeaderRow { get; set; } = 1;
    /// <summary>The first row of data; blank means the row after the header.</summary>
    public int? DataStartRow { get; set; }
    /// <summary>Text files: the separator (a single character), blank to detect it.</summary>
    public string? Delimiter { get; set; }
    /// <summary>"name": a mapped column is found by its heading in each new file, wherever it sits. "position": by its place.</summary>
    public string ColumnBy { get; set; } = ImportSourceKinds.ColumnByName;
    /// <summary>Excel only: read every sheet, one after the other, into the same table. Each sheet must have the columns the import uses;
    /// <see cref="Sheet"/> is then not used.</summary>
    public bool AllSheets { get; set; }
    /// <summary>The column headings of the file the import was set up with, in order. Mappings refer to columns by place in this list.</summary>
    public List<string> Columns { get; set; } = new();

    public ImportFileLayout ToLayout() => new(
        Format, string.IsNullOrWhiteSpace(Sheet) ? null : Sheet, HeaderRow, DataStartRow,
        string.IsNullOrEmpty(Delimiter) ? null : Delimiter == "\\t" ? '\t' : Delimiter[0]);
}

/// <summary>The file a run reads, as the plan builder and chunk reader need it.</summary>
/// <summary>One sheet of a workbook read as part of "every sheet": its own columns (a sheet may order them differently) and size.</summary>
public sealed record ImportFileSheet(string Name, IReadOnlyList<ImportFileColumn> Columns, long DataRows, long LastRow);

/// <param name="Columns">The columns the import's mappings are planned against. For "every sheet" these are the first sheet's.</param>
/// <param name="LastRow">The last row's number, which is also the end of the run's progress. For "every sheet" it is the last row's
/// <see cref="ImportFileSheets.RowId"/>.</param>
/// <param name="Sheets">Set only when every sheet is read.</param>
public sealed record ImportFileSource(
    IReadOnlyList<ImportFileColumn> Columns, string FileName, ImportFileLayout Layout, string StoragePath, long DataRows, long LastRow, ImportFileOptions Options,
    IReadOnlyList<ImportFileSheet>? Sheets = null);

/// <summary>Reading several sheets as one source: where each sheet's columns are, and how a row is numbered across sheets.</summary>
public static class ImportFileSheets
{
    /// <summary>Rows of different sheets share one ordered run cursor, so a row's id is its sheet's turn times this (more than the most
    /// rows a sheet can have) plus its row number. Details files and the run page show the row number and the sheet's name.</summary>
    public const long Stride = 2_097_152;

    public static long RowId(int sheetIndex, long rowNumber) => sheetIndex * Stride + rowNumber;
    public static (int SheetIndex, long RowNumber) Split(long id) => ((int)(id / Stride), id % Stride);

    /// <summary>For each column of the import (by its place in <paramref name="canonical"/>), where it is in this sheet's rows: its place
    /// by that column's heading, or by position. -1 when the sheet does not have it.</summary>
    public static int[] Translate(IReadOnlyList<ImportFileColumn> canonical, IReadOnlyList<ImportFileColumn> sheet, bool byName)
    {
        var map = new int[canonical.Count];
        for (var i = 0; i < canonical.Count; i++)
        {
            if (!byName) { map[i] = i < sheet.Count ? i : -1; continue; }
            var wanted = canonical[i].Name;
            var found = sheet.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));
            map[i] = found is null ? -1 : found.Position - 1;
        }
        return map;
    }

    /// <summary>What is wrong, per sheet, with the columns the import uses (mapped columns, columns formulas refer to, columns the
    /// conditions test): empty when every sheet has them all. A run fails up front with this rather than importing some sheets.</summary>
    public static IReadOnlyList<string> MissingColumns(ImportFileSource file, IReadOnlySet<long> usedFids)
    {
        var problems = new List<string>();
        if (file.Sheets is null) return problems;
        var byName = file.Options.ColumnBy != ImportSourceKinds.ColumnByPosition;
        foreach (var sheet in file.Sheets)
        {
            var map = Translate(file.Columns, sheet.Columns, byName);
            var missing = file.Columns.Where(c => usedFids.Contains(c.Fid) && map[c.Position - 1] < 0).Select(c => $"'{c.Name}'").ToList();
            if (missing.Count > 0)
                problems.Add(byName ? $"Sheet '{sheet.Name}' has no column named {string.Join(", ", missing)}." : $"Sheet '{sheet.Name}' has too few columns (needs {string.Join(", ", missing)}).");
        }
        return problems;
    }
}

/// <summary>Points an import's mappings and conditions at the columns of the file actually being read. In "by name" mode a column is
/// found again by its heading, so a file with the columns in a different order, or with new ones in between, still imports; in
/// "by position" mode a column is the one in the same place.</summary>
public static class ImportFileRemap
{
    private static ValidationException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["Import"] = [message] });

    /// <summary>A copy of the configuration whose column references are valid for <paramref name="file"/>. Fails with every column
    /// the import needs that the file does not have.</summary>
    public static ImportDefinitionConfig Apply(ImportDefinitionConfig cfg, ImportFileSource file)
    {
        var saved = file.Options.Columns;
        var byName = file.Options.ColumnBy != ImportSourceKinds.ColumnByPosition;
        var missing = new List<string>();

        int? Translate(long fid)
        {
            var position = (int)fid - ImportFileColumns.FidBase; // 1-based place in the saved list
            if (fid == 3) return 3;                               // the row number stays the row number
            if (position < 1) return null;
            if (!byName) return position <= file.Columns.Count ? ImportFileColumns.FidOf(position) : Missing($"column {position}");
            if (position > saved.Count) return Missing($"column {position}");
            var wanted = saved[position - 1].Trim();
            var found = file.Columns.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));
            return found?.Fid ?? Missing($"'{wanted}'");
        }

        int? Missing(string what)
        {
            if (!missing.Contains(what)) missing.Add(what);
            return null;
        }

        var mappings = cfg.Mappings.Select(m =>
        {
            var copy = ImportJson.Deserialize<ImportFieldMapping>(ImportJson.Serialize(m))!;
            if (!m.DoNotImport && m.Source == ImportMappingSource.Dynamic && m.SourceFid is { } fid) copy.SourceFid = Translate(fid);
            return copy;
        }).ToList();
        var conditions = Remap(cfg.Conditions, Translate);

        if (missing.Count > 0)
            throw Invalid(byName
                ? $"The file has no column named {string.Join(", ", missing)}. Rename the heading in the file, or edit the import."
                : $"The file has fewer columns than the import expects ({string.Join(", ", missing)} missing).");

        var clone = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(cfg))!;
        clone.Mappings = mappings;
        clone.Conditions = conditions;
        return clone;
    }

    private static FilterGroup? Remap(FilterGroup? group, Func<long, int?> translate)
    {
        if (group is null) return null;
        var nodes = new List<FilterNode>(group.Nodes.Count);
        foreach (var n in group.Nodes)
        {
            var condition = n.Condition;
            if (condition is not null)
            {
                var copy = ImportJson.Deserialize<FilterCondition>(ImportJson.Serialize(condition))!;
                if (condition.FieldId > 3) copy.FieldId = translate(condition.FieldId) ?? condition.FieldId;
                if (condition.ValueFieldId is > 3) copy.ValueFieldId = translate(condition.ValueFieldId.Value) ?? condition.ValueFieldId;
                condition = copy;
            }
            nodes.Add(new FilterNode { Condition = condition, Group = Remap(n.Group, translate) });
        }
        return new FilterGroup { Logic = group.Logic, Nodes = nodes };
    }
}
