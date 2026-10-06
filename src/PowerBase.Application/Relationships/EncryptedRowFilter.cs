using System.Globalization;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Filtering on encrypted columns. SQL sees ciphertext, so a filter tree that reads an encrypted field
/// can't be a WHERE clause; the rows are decrypted and the tree judged in memory instead
/// (<see cref="PipelineFilterEvaluator"/>), and the matching row Ids go back into the SQL query.
/// Used by summaries and by a Reference field's "Filter dropdown values".
/// </summary>
public static class EncryptedRowFilter
{
    /// <summary>A non-system field is encrypted when the app is, or the field is marked so
    /// (FieldEncryptionContext's rule).</summary>
    public static bool IsEncrypted(AppField field, bool appEncrypted) =>
        !field.IsSystem && !PhysicalNaming.IsEncryptionExemptTypeCode(field.TypeCode) && (appEncrypted || field.IsEncrypted);

    /// <summary>True when any condition in the tree reads an encrypted field.</summary>
    public static bool TouchesEncrypted(FilterGroup? tree, IReadOnlyList<AppField> fields, bool appEncrypted)
    {
        if (tree is null) return false;
        foreach (var node in tree.Nodes)
        {
            if (node.Condition is { } c
                && fields.FirstOrDefault(f => f.Fid == c.FieldId) is { } field
                && IsEncrypted(field, appEncrypted)) return true;
            if (TouchesEncrypted(node.Group, fields, appEncrypted)) return true;
        }
        return false;
    }

    /// <summary>The Ids of the (decrypted) rows the tree matches. Relative dates are resolved to
    /// real dates first, as a report run does.</summary>
    /// <param name="computed">Per row (same order as <paramref name="rows"/>), the computed values by Fid — Formula and
    /// Summary fields have no column, so a condition on one is judged against what the record read would show.</param>
    public static IReadOnlyList<long> MatchingIds(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<AppField> fields, FilterGroup tree,
        IReadOnlyList<IReadOnlyDictionary<long, object?>>? computed = null)
    {
        var resolved = RunReportQueryHandler.ResolveDateValueModeConditions(tree) ?? tree;

        // A calculated field is judged as the type it returns (a number compares as a number, not as text).
        var judgedAs = fields.Select(f => SummaryComputedTargets.ResultKind(f) is { } kind
            ? new AppField { Id = f.Id, Fid = f.Fid, Name = f.Name, Label = f.Label, TypeCode = kind == "Bool" ? "Checkbox" : kind }
            : f).ToList();

        var ids = new List<long>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.TryGetValue("Id", out var rawId) || rawId is null) continue;
            var values = new Dictionary<long, object?>(ValuesByFid(row, fields));
            if (computed is not null && i < computed.Count)
                foreach (var (fid, value) in computed[i])
                    values[fid] = value is DateTime dt ? dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : value;
            if (PipelineFilterEvaluator.EvaluateFilterGroup(resolved, values, judgedAs))
                ids.Add(Convert.ToInt64(rawId, CultureInfo.InvariantCulture));
        }
        return ids;
    }

    /// <summary>A lookup has no column. Returns copies of the rows with the lookup's value put under its own column
    /// (taken through the row's reference to the source table, whose rows are given decrypted), and the field list with
    /// the lookup replaced by a field of its source's type (an address part is plain text), so it is compared and
    /// formatted as that type.</summary>
    public static (IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, IReadOnlyList<AppField> Fields) WithLookupValue(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<AppField> fields,
        AppField lookup, PowerBase.Domain.FieldSettings.LookupSettings settings, AppField source,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> sourceRows)
    {
        var sourceById = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        foreach (var r in sourceRows)
            if (r.TryGetValue("Id", out var sid) && EncryptedSummaryAggregator.NormalizeKey(sid) is { } sk) sourceById[sk] = r;

        var sourceCol = Column(source);
        var refCol = PhysicalNaming.ColumnName(settings.ReferenceFid!.Value);
        var lookupCol = PhysicalNaming.ColumnName(lookup.Fid!.Value);
        var copies = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var copy = new Dictionary<string, object?>(row);
            object? value = null;
            if (row.TryGetValue(refCol, out var rawRef) && EncryptedSummaryAggregator.NormalizeKey(rawRef) is { } key
                && sourceById.TryGetValue(key, out var sourceRow) && sourceRow.TryGetValue(sourceCol, out var v) && v is not DBNull)
                value = string.IsNullOrWhiteSpace(settings.SourceSubField) ? v : RelationalProjector.ExtractJsonSubField(v, settings.SourceSubField);
            copy[lookupCol] = value;
            copies.Add(copy);
        }

        var withLookup = fields.ToList();
        var index = withLookup.FindIndex(f => f.Fid == lookup.Fid);
        if (index >= 0)
            withLookup[index] = new AppField
            {
                Id = lookup.Id, Fid = lookup.Fid, Name = lookup.Name, Label = lookup.Label,
                TypeCode = string.IsNullOrWhiteSpace(settings.SourceSubField) ? source.TypeCode : "Text",
                Settings = string.IsNullOrWhiteSpace(settings.SourceSubField) ? source.Settings : null,
            };
        return (copies, withLookup);
    }

    /// <summary>Formula and Summary fields have no column: for those the criteria or the sort read, each row gets the
    /// computed value (<paramref name="computed"/>, per row, by Fid) under the field's own column, and the field is
    /// replaced in the returned list by one of the type it returns — so it is compared and sorted like a stored field.</summary>
    public static (IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, IReadOnlyList<AppField> Fields) WithComputedValues(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<long, object?>> computed, IEnumerable<long> readFids)
    {
        var computedFields = readFids.Distinct()
            .Select(fid => fields.FirstOrDefault(f => f.Fid == fid))
            .Where(f => f is not null && SummaryComputedTargets.ResultKind(f) is not null)
            .Select(f => f!).ToList();
        if (computedFields.Count == 0) return (rows, fields);

        var copies = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var copy = new Dictionary<string, object?>(rows[i]);
            foreach (var f in computedFields)
                copy[PhysicalNaming.ColumnName(f.Fid!.Value)] = i < computed.Count ? computed[i].GetValueOrDefault(f.Fid.Value) : null;
            copies.Add(copy);
        }

        var typed = fields.Select(f => computedFields.Contains(f)
            ? new AppField
            {
                Id = f.Id, Fid = f.Fid, Name = f.Name, Label = f.Label,
                TypeCode = SummaryComputedTargets.ResultKind(f) is { } kind ? (kind == "Bool" ? "Checkbox" : kind) : f.TypeCode,
            }
            : f).ToList();
        return (copies, typed);
    }

    /// <summary>A field's column in a row dictionary: its f_{fid} column, or a system field's own column.</summary>
    public static string Column(AppField f) => f.IsSystem ? Column(f.Fid!.Value) : PhysicalNaming.ColumnName(f.Fid!.Value);

    public static string Column(int fid) => fid switch
    {
        1 => "CreatedOn", 2 => "ModifiedOn", 3 => "Id", 4 => "CreatedBy", 5 => "ModifiedBy",
        _ => PhysicalNaming.ColumnName(fid),
    };

    /// <summary>The row's stored values keyed by Fid, dates as ISO text, for the filter evaluator.</summary>
    public static IReadOnlyDictionary<long, object?> ValuesByFid(IReadOnlyDictionary<string, object?> row, IReadOnlyList<AppField> fields)
    {
        var map = new Dictionary<long, object?>();
        foreach (var f in fields)
        {
            if (!f.Fid.HasValue || PhysicalNaming.IsComputedTypeCode(f.TypeCode)) continue;
            row.TryGetValue(Column(f), out var v);
            map[f.Fid.Value] = v is DateTime dt ? dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : v;
        }
        return map;
    }
}
