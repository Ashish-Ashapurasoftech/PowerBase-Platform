using System.Collections.Generic;
using System.Linq;
using PowerBase.Application.Common.Models;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.FieldReferences.Extractors;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Compute;

/// <summary>
/// Which stored columns get an index (design 4.3): not every c_ column — each index slows the worker's writes — only the Ready fields that
/// a saved report sorts, groups or filters on (dynamic filters and the chart series included), or that a role's Custom Rules filter on.
/// Reads the same field references the field "Usage" tab shows (<see cref="ReportReferenceExtractor"/>), so a report and this list agree.
/// </summary>
public static class StoredIndexPlanner
{
    /// <summary>Report usages an index helps: the ones that become ORDER BY / GROUP BY / WHERE on the column.</summary>
    public static readonly IReadOnlySet<string> IndexedUsages = new HashSet<string>
    {
        FieldReferenceUsages.Sort, FieldReferenceUsages.GroupBy, FieldReferenceUsages.Filter,
        FieldReferenceUsages.DynamicFilter, FieldReferenceUsages.Chart
    };

    /// <summary>A Yes/No column has two values: an index on it is rarely used and still costs every write (design 4.3).</summary>
    public static bool IsWorthIndexing(string sqlType) => sqlType is not ("BIT" or "NVARCHAR(MAX)");   // MAX cannot be an index key

    /// <summary>
    /// The AppField.Id of every field in <paramref name="candidateFieldIds"/> that a report or a role rule sorts, groups or filters on.
    /// <paramref name="fieldsByTable"/> maps a table to its fields (Fid → Id); reports and rules name fields by Fid.
    /// </summary>
    public static IReadOnlySet<long> FieldsToIndex(
        IReadOnlySet<long> candidateFieldIds,
        IReadOnlyDictionary<long, IReadOnlyList<AppField>> fieldsByTable,
        IEnumerable<(long TableId, string? DefinitionJson)> reports,
        IEnumerable<(long TableId, string? FilterJson)> roleRules)
    {
        var wanted = new HashSet<long>();
        if (candidateFieldIds.Count == 0) return wanted;
        var indexes = new Dictionary<long, TableFieldIndex>();
        TableFieldIndex? IndexOf(long tableId)
        {
            if (indexes.TryGetValue(tableId, out var i)) return i;
            if (!fieldsByTable.TryGetValue(tableId, out var fields)) return null;
            return indexes[tableId] = new TableFieldIndex(tableId, fields);
        }

        foreach (var (tableId, json) in reports)
        {
            if (IndexOf(tableId) is not { } table) continue;
            var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.Report, 0, tableId);
            ReportReferenceExtractor.Extract(json, table, collector);
            foreach (var row in collector.Rows)
                if (IndexedUsages.Contains(row.Usage) && candidateFieldIds.Contains(row.TargetFieldId)) wanted.Add(row.TargetFieldId);
        }

        // Role Custom Rules (View and Modify) are a WHERE on every read of the table. Only the group shape names fields by Fid; the legacy
        // flat shape names them by PublicId and is left out.
        foreach (var (tableId, json) in roleRules)
        {
            if (IndexOf(tableId) is not { } table || RoleRecordFilterJson.ParseGroup(json) is not { } group) continue;
            var collector = new FieldReferenceCollector(FieldReferenceSourceTypes.CustomRule, 0, tableId);
            ReportReferenceExtractor.ExtractFilterGroup(group, table, collector);
            foreach (var row in collector.Rows)
                if (row.Usage == FieldReferenceUsages.Filter && candidateFieldIds.Contains(row.TargetFieldId)) wanted.Add(row.TargetFieldId);
        }
        return wanted;
    }
}
