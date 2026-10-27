using PowerBase.Application.Import.Diff;
using PowerBase.Application.Import.Mapping;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Commands.UpdateAppFromPbl;

public sealed class ImportUpdatePlan
{
    public HashSet<string> AddKeys { get; } = new(StringComparer.Ordinal);
    public List<ImportDiffItem> Overwrites { get; } = [];
    public List<ImportElementOutcome> Outcomes { get; } = [];
    public HashSet<string> AddedTableRefs { get; } = new(StringComparer.Ordinal);
    public bool TableAdded(string tableRef) => AddedTableRefs.Contains(tableRef);
}


/// <summary>Turns a diff plus the user's decisions into what will be added/overwritten/skipped, or
/// rejects the request when a decision is missing or not allowed. Pure — touches nothing.</summary>
public static class ImportUpdatePlanner
{
    /// <summary>Turns the diff and the user's decisions into what will be added/overwritten, or
    /// rejects the request when a decision is missing or not allowed.</summary>
    public static ImportUpdatePlan Plan(ImportDiffResult diff, ImportMapping mapping)
    {
        var plan = new ImportUpdatePlan();
        var byKey = diff.Items.ToDictionary(i => ImportSourceRefRecorder.Key(i.ElementType, i.SourceLogicalRef), StringComparer.Ordinal);

        var decisions = new Dictionary<string, ImportMappingElement>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var d in mapping.Elements)
        {
            var key = ImportSourceRefRecorder.Key(d.ElementType, d.SourceLogicalRef);
            if (!byKey.TryGetValue(key, out var item))
            {
                errors.Add($"Decision for unknown element {d.ElementType} '{d.SourceLogicalRef}'.");
                continue;
            }
            if (d.Action == ImportElementAction.Add && item.Status != ImportDiffStatus.New)
                errors.Add($"{d.ElementType} '{item.Name}' already exists in the app and cannot be added.");
            if (d.Action == ImportElementAction.Overwrite && !item.CanOverwrite)
                errors.Add($"{d.ElementType} '{item.Name}' cannot be overwritten by an import" + (item.Reason is null ? "." : $": {item.Reason}"));
            decisions[key] = d;
        }

        var undecided = diff.Items
            .Where(i => i.Status == ImportDiffStatus.Changed && i.CanOverwrite && !decisions.ContainsKey(ImportSourceRefRecorder.Key(i.ElementType, i.SourceLogicalRef)))
            .ToList();

        if (errors.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["Mapping"] = errors.ToArray() });
        if (undecided.Count > 0)
            throw new BadRequestException("UNDECIDED_CHANGED_ELEMENTS",
                $"{undecided.Count} changed element(s) need an explicit Overwrite or Skip decision: " +
                string.Join(", ", undecided.Take(10).Select(i => $"{i.ElementType} '{i.Name}'")) + (undecided.Count > 10 ? ", …" : "."));

        foreach (var item in diff.Items)
        {
            var key = ImportSourceRefRecorder.Key(item.ElementType, item.SourceLogicalRef);
            decisions.TryGetValue(key, out var decision);

            switch (item.Status)
            {
                case ImportDiffStatus.New:
                    if (decision?.Action == ImportElementAction.Skip)
                        plan.Outcomes.Add(Outcome(item, ImportOutcome.Skipped, "Skipped by user."));
                    else
                        plan.AddKeys.Add(key);
                    break;
                case ImportDiffStatus.Changed:
                    if (decision!.Action == ImportElementAction.Overwrite)
                        plan.Overwrites.Add(item);
                    else
                        plan.Outcomes.Add(Outcome(item, ImportOutcome.Skipped, "Skipped by user."));
                    break;
                case ImportDiffStatus.Conflict:
                    plan.Outcomes.Add(Outcome(item, ImportOutcome.Skipped, item.Reason));
                    break;
                default:
                    plan.Outcomes.Add(Outcome(item, ImportOutcome.Unchanged, null));
                    break;
            }
        }

        // An element can't be added under a table that isn't being added.
        var newTableItems = diff.Items.Where(i => i.ElementType == ImportElementTypes.Table && i.Status == ImportDiffStatus.New).ToList();
        var blockedTableRefs = newTableItems
            .Where(t => !plan.AddKeys.Contains(ImportSourceRefRecorder.Key(ImportElementTypes.Table, t.SourceLogicalRef)))
            .Select(t => t.SourceLogicalRef).ToHashSet(StringComparer.Ordinal);
        foreach (var t in newTableItems.Where(t => !blockedTableRefs.Contains(t.SourceLogicalRef)))
            plan.AddedTableRefs.Add(t.SourceLogicalRef);

        foreach (var item in diff.Items.Where(i => i.Status == ImportDiffStatus.New && i.ElementType != ImportElementTypes.Table && blockedTableRefs.Contains(i.TableRef)))
        {
            if (plan.AddKeys.Remove(ImportSourceRefRecorder.Key(item.ElementType, item.SourceLogicalRef)))
                plan.Outcomes.Add(Outcome(item, ImportOutcome.Skipped, "Its table was not added."));
        }

        return plan;
    }

    public static ImportElementOutcome Outcome(ImportDiffItem item, string outcome, string? reason) => new()
    {
        ElementType = item.ElementType, SourceLogicalRef = item.SourceLogicalRef, Name = item.Name,
        TableName = item.TableName, Outcome = outcome, Reason = reason,
    };
}
