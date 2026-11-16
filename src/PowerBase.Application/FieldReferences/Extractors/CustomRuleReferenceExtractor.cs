using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.FieldReferences.Extractors;

/// <summary>Every field one Custom Rules row (meta.AppRoleRecordFilter) points at. A row holds one of two shapes
/// (see <see cref="RoleRecordFilterJson"/>): a nested group whose fields are numeric Fids, or the legacy flat list
/// whose fields are public ids. Both "which records a role sees" (View) and "which it may edit or delete" (Modify)
/// count — deleting a field either one reads would silently change what the role is allowed to do.</summary>
public static class CustomRuleReferenceExtractor
{
    public static void Extract(CustomRuleSource rule, TableFieldIndex table, FieldReferenceCollector collector)
    {
        var usage = string.Equals(rule.Kind, RoleRecordFilterJson.ModifyKind, StringComparison.OrdinalIgnoreCase)
            ? FieldReferenceUsages.ModifyRule
            : FieldReferenceUsages.ViewRule;

        if (RoleRecordFilterJson.ParseGroup(rule.FilterJson) is { } group)
        {
            ReportReferenceExtractor.ExtractFilterGroup(group, table, collector, usage);
            return;
        }

        var idByPublicId = table.Fields.ToDictionary(f => f.PublicId, f => f.Id);
        foreach (var condition in RoleRecordFilterJson.ParseLegacyConditions(rule.FilterJson))
            if (idByPublicId.TryGetValue(condition.FieldPublicId, out var fieldId))
                collector.Add(fieldId, usage);
    }
}
