using PowerBase.Application.Imports;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Fields;

/// <summary>
/// Rules for the Snapshot setting: a field whose value is copied once from another field of the
/// same record when the record is created, and is read-only afterwards (unless Editable after
/// snapshot is on). The destination list is a strict allow-list, like <see cref="FieldAutoFillCapability"/>:
/// derived, structured, attachment and system values can never be a snapshot.
/// </summary>
public static class FieldSnapshotCapability
{
    private static readonly HashSet<string> SupportedDestinationTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "TextMultiLine",
        "Number", "Currency", "Percent",
        "Date", "DateTime", "Time",
        "Boolean",
        "Phone", "Email", "Url",
    };

    public static bool IsSupportedDestination(string typeCode) => SupportedDestinationTypeCodes.Contains(typeCode);

    /// <summary>A field whose value can be captured: a stored field, or a Lookup (whose value is read from the related table at the time the record is created).</summary>
    public static bool IsEligibleSource(AppField f) =>
        f.Fid.HasValue && !f.IsDeleted && !f.IsSystem
        && (SummaryLookupSources.IsLookup(f) || !PowerBase.Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode))
        && !string.Equals(f.TypeCode, "File", StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates the Snapshot configuration of <paramref name="destination"/>. Turning it off
    /// (<paramref name="sourceFid"/> null) is always allowed, so a legacy row is never blocked by this.</summary>
    public static IDictionary<string, string[]> Validate(
        AppField destination, string label, int? sourceFid, bool editable,
        bool isRequired, bool isUnique, bool isEncrypted, bool isAutoFill,
        IReadOnlyList<AppField> tableFields)
    {
        var errors = new Dictionary<string, string[]>();
        if (sourceFid is null) return errors;

        if (destination.IsSystem || !IsSupportedDestination(destination.TypeCode))
        {
            errors["SnapshotSourceFid"] = [$"'{label}' fields of this type do not support Snapshot."];
            return errors;
        }

        var source = tableFields.FirstOrDefault(f => f.Fid == sourceFid && !f.IsDeleted);
        if (source is null || source.Id == destination.Id)
        {
            errors["SnapshotSourceFid"] = ["Choose a different field of this table to take the snapshot from."];
            return errors;
        }
        if (!IsEligibleSource(source))
        {
            errors["SnapshotSourceFid"] = [$"'{ImportTypeCompatibility.DisplayName(source)}' cannot be used as a snapshot source."];
            return errors;
        }

        var mismatch = ImportTypeCompatibility.CheckMapping(source, destination);
        if (mismatch is not null)
        {
            errors["SnapshotSourceFid"] = [mismatch.Replace("cannot be imported into", "cannot be captured into")];
            return errors;
        }

        if (isUnique)
            errors["IsUnique"] = ["A Snapshot field cannot also be unique."];
        if (isEncrypted)
            errors["IsEncrypted"] = ["A Snapshot field cannot be encrypted."];
        if (isAutoFill)
            errors["IsAutoFill"] = ["A Snapshot field cannot use Auto-fill — a copied record takes a fresh snapshot instead."];
        // A blank source would fail every record create if the snapshot had to be non-empty.
        if (isRequired && !editable)
            errors["IsRequired"] = ["A Snapshot field can only be required when it is editable after the snapshot."];

        return errors;
    }
}
