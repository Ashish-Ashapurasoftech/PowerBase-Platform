using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships.Queries;

/// <summary>The values a picked parent record fills into the form fields its Reference's dropdown depends on: with "show
/// all until chosen" (<see cref="ReferenceSettings.ShowAllWhenBlank"/>) an Activity can be picked before the Task Type,
/// and then fills the Task Type with its own. For each
/// "parent field equals form field" comparison (<see cref="ReferenceFilterResolver.EqualityPairs"/>), the parent record's
/// value of that field, keyed by the form field's Fid. Empty when "show all" or the filter is off, or the record can't be
/// found or its table viewed. The client fills only fields that are still blank.</summary>
public class GetControllingFillQueryHandler
{
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer? _enforcer;

    public GetControllingFillQueryHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer? enforcer = null)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
    }

    /// <param name="parentRecord">The picked parent: its row Id, PublicId, or key text (see <see cref="ParentRecordResolver"/>).</param>
    public async Task<IReadOnlyDictionary<int, string>> HandleAsync(Guid relationshipPublicId, string parentRecord, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);
        var none = new Dictionary<int, string>();

        var childFields = await _fieldRepo.ListByTableAsync(rel.ChildTableId, ct);
        var referenceField = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId);
        var settings = FormulaTypeMap.ParseReferenceSettings(referenceField?.Settings);
        if (referenceField is null || settings?.ShowAllWhenBlank != true || !ReferenceFilterResolver.HasFilter(settings))
            return none;

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
        var parentRowId = await ParentRecordResolver.ResolveRowIdAsync(rel, parent, parentRecord, _recordRepo, _fieldRepo, ct);
        if (parentRowId <= 0) return none;

        // Only parent fields the user's role may see, and only stored ones on both sides (a computed field has no
        // column to read, and can't be filled).
        var access = _enforcer is null ? null : await _enforcer.GetTableAccessAsync(parent, parentFields, ct);
        if (access is { CanView: false }) return none;
        bool Visible(AppField f) => access is null || access.Unrestricted || access.VisibleFields.Any(v => v.Id == f.Id);

        var pairs = ReferenceFilterResolver.EqualityPairs(settings)
            .Select(p => (Parent: parentFields.FirstOrDefault(f => f.Fid == p.ParentFid), Form: childFields.FirstOrDefault(f => f.Fid == p.FormFid)))
            .Where(p => p.Parent is not null && p.Form is not null && p.Form.Id != referenceField.Id
                && !PhysicalNaming.IsComputedTypeCode(p.Parent.TypeCode) && !PhysicalNaming.IsComputedTypeCode(p.Form.TypeCode)
                && Visible(p.Parent))
            .Select(p => (Parent: p.Parent!, Form: p.Form!))
            .ToList();
        if (pairs.Count == 0) return none;

        var rows = await _recordRepo.GetRowsByIdsAsync(parent, pairs.Select(p => p.Parent).Distinct().ToList(), [parentRowId], ct);
        if (!rows.TryGetValue(parentRowId, out var row)) return none;

        var values = new Dictionary<int, string>();
        foreach (var (parentField, formField) in pairs)
        {
            if (row.TryGetValue(PhysicalNaming.GetPhysicalColumnName(parentField), out var raw) && FillText(raw) is { } text)
                values.TryAdd(formField.Fid!.Value, text);
        }
        return values;
    }

    /// <summary>A stored value as the text a form control takes: a date as ISO, a number with a '.' decimal point and no
    /// padding zeros (a DECIMAL column reads back "5.0000"), a checkbox as true/false. Null for blank.</summary>
    private static string? FillText(object? value)
    {
        var inv = CultureInfo.InvariantCulture;
        return value switch
        {
            null or DBNull => null,
            string s => string.IsNullOrWhiteSpace(s) ? null : s,
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", inv) : d.ToString("yyyy-MM-ddTHH:mm:ss", inv),
            bool b => b ? "true" : "false",
            decimal m => m.ToString("0.##########", inv),
            IFormattable f => f.ToString(null, inv),
            _ => value.ToString(),
        };
    }
}
