using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Blocks an edit that changes the type of value a Summary field produces (e.g. Count → Combined
/// Text turns a number into text) while something is built on that value: a formula on the
/// parent table that reads it, a lookup that pulls it down to a child table, or a report that
/// filters on it. Those were authored against the old type and would silently break.
///
/// Edits that keep the result type — the matching criteria, the label, Combined Text options,
/// Sum ↔ Avg, a different numeric target — are always allowed.
/// </summary>
public static class SummaryResultTypeGuard
{
    public static async Task EnsureResultTypeChangeIsSafeAsync(
        AppField summary,
        AppTable parentTable,
        string newSettingsJson,
        FormulaEngine engine,
        IRelationshipRepository relRepo,
        IAppFieldRepository fieldRepo,
        CancellationToken ct)
    {
        var oldType = FormulaTypeMap.FieldType(summary.TypeCode, summary.Settings);
        var newType = FormulaTypeMap.FieldType(summary.TypeCode, newSettingsJson);
        if (oldType == newType || summary.Fid is not int fid) return;

        var dependents = new List<string>();

        // Formulas on the parent table that read this summary.
        var parentFields = await fieldRepo.ListByTableAsync(parentTable.Id, ct);
        var schema = new AppFieldSchema(parentFields);
        foreach (var f in parentFields)
        {
            if (f.Id == summary.Id || !FormulaTypeMap.IsFormulaComputed(f.TypeCode, f.Settings)) continue;
            if (FormulaTypeMap.ExpressionAndType(f.TypeCode, f.Settings) is not { } et) continue;
            if (engine.Compile(et.Expression, schema, et.Type).ReferencedFieldIds.Contains(fid))
                dependents.Add($"formula '{Display(f)}'");
        }

        // Lookups that pull this summary down into a child table.
        foreach (var rel in await relRepo.ListByParentTableAsync(parentTable.Id, ct))
        {
            foreach (var f in await fieldRepo.ListByTableAsync(rel.ChildTableId, ct))
            {
                if (f.TypeCode != nameof(Domain.Enums.FieldTypeCode.Lookup)) continue;
                var ls = FormulaTypeMap.ParseLookupSettings(f.Settings);
                if (ls?.RelationshipId == rel.Id && ls.SourceFid == fid)
                    dependents.Add($"lookup '{Display(f)}'");
            }
        }

        // Summaries on a grandparent table that summarize this one (Project › Tasks › Time
        // Entries): the new result must still support their function.
        var asNew = new AppField
        {
            Id = summary.Id, Fid = summary.Fid, Name = summary.Name, Label = summary.Label,
            TypeCode = summary.TypeCode, Settings = newSettingsJson,
        };
        foreach (var rel in await relRepo.ListByChildTableAsync(parentTable.Id, ct))
        {
            foreach (var f in await fieldRepo.ListByTableAsync(rel.ParentTableId, ct))
            {
                if (f.TypeCode != nameof(Domain.Enums.FieldTypeCode.Summary)) continue;
                var s = FormulaTypeMap.ParseSummarySettings(f.Settings);
                var function = SummaryFunctions.Normalize(s?.Function);
                if (s is null || function is null || s.TargetFid != fid || s.ChildTableId != parentTable.Id
                    || s.ReferenceFid != rel.ReferenceFid) continue;
                if (SummaryTargetValidator.FindProblem(function, asNew, s.TargetSubField) is not null)
                    dependents.Add($"summary '{Display(f)}'");
            }
        }

        // Reports whose filter uses this summary (columns/sort/group-by work with any type).
        var usage = await fieldRepo.GetFieldUsageAsync(parentTable.Id, summary.Id, fid, parentTable.AppId, ct);
        foreach (var r in usage.Reports.Where(r => r.UsedAs.Contains("filter")))
            dependents.Add($"report filter '{r.Name}'");

        if (dependents.Count == 0) return;

        throw new ConflictException(
            $"Can't change '{Display(summary)}' from a {oldType} result to a {newType} result: it is used by "
            + string.Join("; ", dependents)
            + ". Keep a calculation with the same result type, or update those first.");
    }

    private static string Display(AppField f) => string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label!;
}
