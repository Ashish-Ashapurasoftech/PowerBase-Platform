using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// The parent side of a Summary field's matching criteria: lets a condition on a child field
/// compare against a field of the child's own parent record (ValueMode <see cref="ValueMode"/>,
/// ValueFieldId = the parent field's Fid) — e.g. "Session Date is on or after the Project's
/// Start Date". Each child is compared with the parent it references, so one batched summary
/// query still serves every parent on the page. A blank parent value matches no children (plain
/// SQL NULL comparison, same as "the value in the field").
/// </summary>
/// <param name="ParentTableId">The parent table (the one the summary field lives on).</param>
/// <param name="ReferenceFid">The child's reference field — its column stores the parent's row Id.</param>
/// <param name="ParentFieldsByFid">The parent table's fields by Fid, to resolve ValueFieldId.</param>
public sealed record ParentFieldScope(
    long ParentTableId,
    int ReferenceFid,
    IReadOnlyDictionary<long, AppField> ParentFieldsByFid)
{
    /// <summary>The FilterCondition.ValueMode for "the value in the parent's field".</summary>
    public const string ValueMode = "parentField";

    /// <summary>The values of the parent's Lookup / Summary / Formula fields the criteria compare to, worked out for
    /// the parents being summarized: parent field Fid → parent row Id → value. Those fields have no column to read.</summary>
    public IReadOnlyDictionary<long, IReadOnlyDictionary<long, object?>>? ComputedValues { get; init; }

    public static bool IsParentFieldMode(string? valueMode) =>
        string.Equals(valueMode, ValueMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a condition can compare to <paramref name="parentField"/>: a stored or system field, a
    /// Lookup (of anything but a range, whose end has no single value, or a link — a Report Link or URL formula), or a
    /// Formula / Summary whose result can be compared (<see cref="SummaryComputedTargets.ResultKind"/>). The calculated
    /// ones have no column, so their values are worked out per parent first (<see cref="ComputedValues"/>).</summary>
    public static bool IsComparable(AppField parentField) =>
        !PhysicalNaming.IsComputedTypeCode(parentField.TypeCode)
        || (SummaryLookupSources.IsLookup(parentField)
            && SummaryLookupSources.Settings(parentField)?.SourceTypeCode is var source
            && !PhysicalNaming.IsRangeTypeCode(source ?? string.Empty)
            && source is not ("ReportLink" or "Formula_Url"))
        || SummaryComputedTargets.ResultKind(parentField) is not null;
}
