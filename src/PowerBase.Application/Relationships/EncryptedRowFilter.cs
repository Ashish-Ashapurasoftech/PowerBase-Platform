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

    /// <summary>True when any condition in the tree reads an encrypted field — the one it filters on, or the other
    /// field of a "the value in the field" comparison.</summary>
    public static bool TouchesEncrypted(FilterGroup? tree, IReadOnlyList<AppField> fields, bool appEncrypted)
    {
        if (tree is null) return false;
        bool Encrypted(long fid) => fields.FirstOrDefault(f => f.Fid == fid) is { } field && IsEncrypted(field, appEncrypted);
        foreach (var node in tree.Nodes)
        {
            if (node.Condition is { } c
                && (Encrypted(c.FieldId)
                    || (string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase) && c.ValueFieldId is long other && Encrypted(other))))
                return true;
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

        // Read as the SQL filter reads it: one part of an Address ("City") as its own text value instead of the whole address,
        // and "ends with" (the in-memory evaluator has no case for it) as the wildcard match it equals, on the value as one
        // line (a wildcard does not run across line breaks).
        var standIns = new List<StandIn>();
        resolved = ForMemory(resolved, fields, standIns);

        // A calculated field is judged as the type it returns (a number compares as a number, not as text).
        var judgedAs = fields.Select(f => SummaryComputedTargets.ResultKind(f) is { } kind
            ? new AppField { Id = f.Id, Fid = f.Fid, Name = f.Name, Label = f.Label, TypeCode = kind == "Bool" ? "Checkbox" : kind }
            : f).ToList();
        foreach (var s in standIns)
            judgedAs.Add(new AppField { Id = s.StandInFid, Fid = s.StandInFid, Name = $"{s.SourceFid}.{s.Part ?? "line"}", TypeCode = "Text" });

        var ids = new List<long>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.TryGetValue("Id", out var rawId) || rawId is null) continue;
            var values = new Dictionary<long, object?>(ValuesByFid(row, fields));
            if (computed is not null && i < computed.Count)
                foreach (var (fid, value) in computed[i])
                    values[fid] = value is DateTime dt ? dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : value;
            // After the computed values: a stand-in may read a Formula / Lookup value. In list order: a one-line stand-in may
            // read an address-part stand-in made before it.
            foreach (var s in standIns)
                values[s.StandInFid] = s.ValueFrom(values.GetValueOrDefault(s.SourceFid));
            if (PipelineFilterEvaluator.EvaluateFilterGroup(resolved, values, judgedAs))
                ids.Add(Convert.ToInt64(rawId, CultureInfo.InvariantCulture));
        }
        return ids;
    }

    /// <summary>A text value a condition is judged on in place of the field's own, under <see cref="StandInFid"/> — a Fid no
    /// real field has: one part of an Address (<see cref="Part"/>, "city"), and/or the value as one line
    /// (<see cref="OneLine"/>: line breaks read as spaces, Rich Text as the text it shows).</summary>
    private sealed record StandIn(long SourceFid, string? Part, bool OneLine, bool RichText, int StandInFid)
    {
        public object? ValueFrom(object? source)
        {
            var value = Part is null ? source : RelationalProjector.ExtractJsonSubField(source, Part);
            if (!OneLine) return value;
            var text = PipelineFilterEvaluator.ValueText(value);
            return RichText ? PipelineFilterEvaluator.RichTextPlain(text) : AsOneLine(text);
        }
    }

    /// <summary>Above every real Fid: stand-in fields count down from here.</summary>
    private const int StandInFidBase = 2_000_000_000;

    private static int StandInFor(List<StandIn> standIns, long sourceFid, string? part, bool oneLine, bool richText)
    {
        var found = standIns.FirstOrDefault(s => s.SourceFid == sourceFid && s.OneLine == oneLine
            && string.Equals(s.Part, part, StringComparison.OrdinalIgnoreCase));
        if (found is not null) return found.StandInFid;
        var made = new StandIn(sourceFid, part, oneLine, richText, StandInFidBase - standIns.Count);
        standIns.Add(made);
        return made.StandInFid;
    }

    /// <summary>A copy of the tree the in-memory evaluator judges as the SQL filter would: a condition on one part of an
    /// Address moved onto a stand-in holding just that part, and "ends with" / "does not end with" as the wildcard match
    /// "*text" (the text's own '*' and '?' escaped) on a one-line stand-in of the value. A File keeps its "ends with": the
    /// evaluator matches it against the file names itself. Stand-ins are collected in <paramref name="standIns"/>.</summary>
    private static FilterGroup ForMemory(FilterGroup group, IReadOnlyList<AppField> fields, List<StandIn> standIns) => new()
    {
        Logic = group.Logic,
        Nodes = group.Nodes.Select(n => n.Condition is { } c
            ? new FilterNode { Condition = ForMemory(c, fields, standIns) }
            : new FilterNode { Group = n.Group is null ? null : ForMemory(n.Group, fields, standIns) }).ToList(),
    };

    private static FilterCondition ForMemory(FilterCondition c, IReadOnlyList<AppField> fields, List<StandIn> standIns)
    {
        var field = fields.FirstOrDefault(f => f.Fid == c.FieldId);
        var fieldId = c.FieldId;
        var subField = c.SubField;
        var op = c.Operator;
        var value = c.Value;
        if (!string.IsNullOrWhiteSpace(subField) && field is { TypeCode: "Address" })
            (fieldId, subField) = (StandInFor(standIns, c.FieldId, subField, oneLine: false, richText: false), null);

        if (op is "endsWith" or "notEndsWith" && !PipelineFilterEvaluator.IsFileField(field))
        {
            var richText = fieldId == c.FieldId && PipelineFilterEvaluator.IsRichTextField(field);
            fieldId = StandInFor(standIns, fieldId, null, oneLine: true, richText);
            op = op == "endsWith" ? "wildcard" : "notWildcard";
            value = "*" + EscapeWildcard(AsOneLine(c.Value));
        }

        if (fieldId == c.FieldId && op == c.Operator) return c;
        return new FilterCondition
        {
            FieldId = fieldId, Operator = op, Value = value, SubField = subField,
            ValueMode = c.ValueMode, ValueFieldId = c.ValueFieldId,
        };
    }

    /// <summary>Text as a wildcard pattern that matches only itself: '*' and '?' escaped.</summary>
    private static string EscapeWildcard(string? text) =>
        (text ?? string.Empty).Replace("*", "\\*").Replace("?", "\\?");

    /// <summary>Line breaks read as spaces, so a wildcard (whose '*' does not run across a line break) sees one line.</summary>
    private static string AsOneLine(string? text) =>
        (text ?? string.Empty).Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

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
        // A looked-up range brings its end along too (Combined Text shows "start – end").
        var withEnd = string.IsNullOrWhiteSpace(settings.SourceSubField) && PhysicalNaming.IsRangeTypeCode(source.TypeCode)
            && source.Fid.HasValue;
        var sourceEndCol = withEnd ? PhysicalNaming.EndColumnName(source.Fid!.Value) : null;
        var lookupEndCol = withEnd ? PhysicalNaming.EndColumnName(lookup.Fid!.Value) : null;
        var copies = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var copy = new Dictionary<string, object?>(row);
            object? value = null;
            object? end = null;
            if (row.TryGetValue(refCol, out var rawRef) && EncryptedSummaryAggregator.NormalizeKey(rawRef) is { } key
                && sourceById.TryGetValue(key, out var sourceRow))
            {
                if (sourceRow.TryGetValue(sourceCol, out var v) && v is not DBNull)
                    value = string.IsNullOrWhiteSpace(settings.SourceSubField) ? v : RelationalProjector.ExtractJsonSubField(v, settings.SourceSubField);
                if (sourceEndCol is not null && sourceRow.TryGetValue(sourceEndCol, out var e) && e is not DBNull)
                    end = e;
            }
            copy[lookupCol] = value;
            if (lookupEndCol is not null) copy[lookupEndCol] = end;
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

    /// <summary>Formula and Summary fields — and lookups of them (<paramref name="lookupSources"/>) — have no column: for
    /// those the criteria or the sort read, each row gets the computed value (<paramref name="computed"/>, per row, by
    /// Fid) under the field's own column, and the field is replaced in the returned list by one of the type it returns —
    /// so it is compared and sorted like a stored field.</summary>
    public static (IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, IReadOnlyList<AppField> Fields) WithComputedValues(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<long, object?>> computed, IEnumerable<long> readFids,
        IReadOnlyDictionary<long, AppField>? lookupSources = null)
    {
        string? KindOf(AppField f) => SummaryComputedTargets.ResultKind(f) ?? SummaryLookupSources.ComputedValueKind(f, lookupSources);
        var computedFields = readFids.Distinct()
            .Select(fid => fields.FirstOrDefault(f => f.Fid == fid))
            .Where(f => f is not null && KindOf(f) is not null)
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
                TypeCode = KindOf(f) is { } kind ? (kind == "Bool" ? "Checkbox" : kind) : f.TypeCode,
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
