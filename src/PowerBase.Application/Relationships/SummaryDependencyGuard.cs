using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Blocks a field type change that would break Summary fields built on the field. Summaries live on
/// parent tables and target a child field (e.g. Client › "Sum of Amount" over Task.Amount); if Amount
/// became a type the summary's function can't aggregate, every read of that summary would be wrong or
/// fail. The rule is the same one enforced when a summary is created
/// (<see cref="SummaryTargetValidator.FindProblem"/>), applied to the field's new type.
///
/// Call it before persisting any TypeCode change on a field that can be a summary target — today the
/// numeric "Display As" switch (UpdateField) and converting a Number field into a relationship's
/// Reference (CreateRelationship). Reverting a Reference back to Number needs no check: Number
/// supports every summary function a Reference does.
/// </summary>
public static class SummaryDependencyGuard
{
    public static async Task EnsureTypeChangeKeepsSummariesValidAsync(
        AppField field,
        string newTypeCode,
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        CancellationToken ct)
    {
        if (field.Fid is not int fid || string.Equals(field.TypeCode, newTypeCode, StringComparison.OrdinalIgnoreCase))
            return;

        // The field as it would be after the change — only the type differs.
        var asNewType = new AppField
        {
            Id = field.Id, Fid = field.Fid, Name = field.Name, Label = field.Label,
            TypeCode = newTypeCode, IsSystem = field.IsSystem, Settings = field.Settings,
        };

        var broken = new List<string>();
        // Summaries over this field sit on the parent side of relationships where its table is the child.
        foreach (var rel in await relRepo.ListByChildTableAsync(field.AppTableId, ct))
        {
            AppTable? parentTable = null;
            foreach (var summary in (await fieldRepo.ListByTableAsync(rel.ParentTableId, ct))
                         .Where(f => f.TypeCode == nameof(Domain.Enums.FieldTypeCode.Summary)))
            {
                var s = FormulaTypeMap.ParseSummarySettings(summary.Settings);
                var function = SummaryFunctions.Normalize(s?.Function);
                if (s is null || s.TargetFid != fid || s.ChildTableId != field.AppTableId
                    || s.ReferenceFid != rel.ReferenceFid || function is null)
                    continue;
                if (SummaryTargetValidator.FindProblem(function, asNewType, s.TargetSubField) is null)
                    continue;

                parentTable ??= await tableRepo.GetByIdAsync(rel.ParentTableId, ct);
                var summaryLabel = string.IsNullOrWhiteSpace(summary.Label) ? summary.Name : summary.Label;
                broken.Add($"{parentTable.Name} › {summaryLabel} ({FunctionLabel(function)})");
            }
        }

        if (broken.Count == 0) return;

        var fieldLabel = string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;
        throw new ConflictException(
            $"Can't change '{fieldLabel}' to {newTypeCode}: {broken.Count} summary field(s) use it in a way {SummaryTargetValidator.WithArticle(newTypeCode)} field can't support — "
            + string.Join("; ", broken)
            + ". Change or remove these summary fields first.");
    }

    private static string FunctionLabel(string function) => function switch
    {
        SummaryFunctions.Avg => "Average",
        SummaryFunctions.Min => "Minimum",
        SummaryFunctions.Max => "Maximum",
        SummaryFunctions.DistinctCount => "Distinct Count",
        SummaryFunctions.CombinedText => "Combined Text",
        _ => function,
    };
}
