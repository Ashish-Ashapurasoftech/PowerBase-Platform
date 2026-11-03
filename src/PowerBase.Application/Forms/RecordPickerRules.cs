using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Forms;

/// <summary>
/// The one rule set for a form element's "Select using record picker" choice, used when a form layout is saved
/// and when the picker options are read. The choice only applies to a Reference field or its proxy; its target is
/// a shared Table report on the PARENT table, or a Lookup field of THIS relationship.
/// </summary>
public static class RecordPickerRules
{
    /// <summary>A normalized choice. <see cref="Default"/> (all null) is what every element without a choice uses.</summary>
    public readonly record struct Choice(string? Mode, Guid? ReportPublicId, long? FieldId)
    {
        public static readonly Choice Default = new(null, null, null);
    }

    /// <summary>The relationship whose parent records the element picks: the one whose Reference field it is, or whose proxy it is.</summary>
    public static Relationship? RelationshipFor(AppField field, IEnumerable<Relationship> childRelationships) =>
        childRelationships.FirstOrDefault(r => !r.IsDeleted && (r.ReferenceFieldId == field.Id || r.ProxyFieldId == field.Id));

    /// <summary>A report the dropdown may be based on: a non-embedded, non-personal Table report on the parent table.</summary>
    public static bool IsEligibleReport(Report report, long parentTableId) =>
        !report.IsDeleted
        && report.AppTableId == parentTableId
        && report.ReportType == "Table"
        && report.RelationshipId is null
        && !report.IsDefaultSettingsRecord
        && report.Visibility != "Personal";

    /// <summary>A field the dropdown may be based on: a Lookup of this relationship (formulas are not offered, as in Quickbase).</summary>
    public static bool IsEligibleLookup(AppField field, Relationship relationship) =>
        field.TypeCode == "Lookup" && FormulaTypeMap.ParseLookupSettings(field.Settings)?.RelationshipId == relationship.Id;

    /// <summary>
    /// Checks a saved layout element's choice and returns what to store. A choice on an element that is not a Reference or its
    /// proxy, or whose report/lookup no longer exists, is dropped (the element falls back to the default picker) so a stale
    /// choice never blocks saving a form. A report of another table or a field that is not a lookup of this relationship is a 400.
    /// </summary>
    public static async Task<Choice> NormalizeAsync(
        string elementType, string? mode, Guid? reportPublicId, long? fieldId, AppField? elementField,
        IReadOnlyList<AppField> tableFields, IReadOnlyList<Relationship> childRelationships,
        IReportRepository? reportRepo, CancellationToken ct)
    {
        var normalized = mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized == RecordPickerModes.Default)
            return Choice.Default;

        if (normalized is not (RecordPickerModes.Report or RecordPickerModes.Field))
            throw Invalid($"Unknown record picker mode '{mode}'.");

        if (elementType != "Field" || elementField is null || RelationshipFor(elementField, childRelationships) is not { } rel)
            return Choice.Default;

        if (normalized == RecordPickerModes.Report)
        {
            if (reportPublicId is not { } rid)
                throw Invalid("Choose a report for the record picker.");
            if (reportRepo is null)
                return new Choice(RecordPickerModes.Report, rid, null);

            Report report;
            try { report = await reportRepo.GetByPublicIdAsync(rid, ct); }
            catch (NotFoundException) { return Choice.Default; }

            if (!IsEligibleReport(report, rel.ParentTableId))
                throw Invalid("The record picker report must be a shared Table report on the parent table.");
            return new Choice(RecordPickerModes.Report, rid, null);
        }

        if (fieldId is not { } fid)
            throw Invalid("Choose a field for the record picker.");
        var lookup = tableFields.FirstOrDefault(f => f.Fid.HasValue && f.Fid.Value == fid);
        if (lookup is null) return Choice.Default;
        if (!IsEligibleLookup(lookup, rel))
            throw Invalid("The record picker field must be a lookup field of this relationship.");
        return new Choice(RecordPickerModes.Field, null, fid);
    }

    private static ValidationException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["Elements"] = [message] });
}
