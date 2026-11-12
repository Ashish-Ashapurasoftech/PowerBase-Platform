using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula.Binding;

namespace PowerBase.Application.QbMigration;

/// <summary>Pairs each QuickBase field of a table with the PowerBase field the QBL import created for it.
/// The QBL import keeps QuickBase field ids as PowerBase Fids, so the pairing is by number, then checked
/// on type (and label, as a warning). Anything that cannot be written safely blocks the table instead of
/// being guessed.</summary>
public static class QbFieldMapper
{
    /// <summary>Text the QBL import puts in the description of a field it could not create properly.</summary>
    public const string PlaceholderMarker = "Placeholder created by the import";

    // QuickBase field type -> the PowerBase type codes its values can be written into.
    private static readonly Dictionary<string, string[]> Compatible = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = ["Text", "TextMultiLine", "RichText", "SingleSelect", "Email", "Phone", "Url", "Reference"],
        ["text-multi-line"] = ["TextMultiLine", "Text", "RichText"],
        ["rich-text"] = ["RichText", "TextMultiLine", "Text"],
        ["text-multiple-choice"] = ["SingleSelect", "Text"],
        ["multitext"] = ["MultiSelect"],
        ["numeric"] = ["Number", "Currency", "Percent", "Rating", "Reference"],
        ["currency"] = ["Currency", "Number"],
        ["percent"] = ["Percent", "Number"],
        ["rating"] = ["Rating", "Number"],
        ["date"] = ["Date", "DateTime"],
        ["timestamp"] = ["DateTime"],
        ["timeofday"] = ["Time"],
        ["duration"] = ["Duration"],
        ["checkbox"] = ["Boolean"],
        ["email"] = ["Email", "Text"],
        ["phone"] = ["Phone", "Text"],
        ["url"] = ["Url", "Text"],
        ["address"] = ["Address"],
        ["user"] = ["User"],
        ["multiuser"] = ["MultiUser"],
        ["file"] = ["File"],
    };

    public static IReadOnlyList<QbFieldMapping> Map(IReadOnlyList<QbField> qbFields, IReadOnlyList<AppField> pbFields)
    {
        var pbByFid = pbFields.Where(f => !f.IsDeleted && f.Fid.HasValue)
            .GroupBy(f => f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        var result = new List<QbFieldMapping>(qbFields.Count);
        foreach (var qb in qbFields)
            result.Add(MapOne(qb, pbByFid));
        return result;
    }

    private static QbFieldMapping MapOne(QbField qb, Dictionary<int, AppField> pbByFid)
    {
        pbByFid.TryGetValue(qb.Id, out var pb);

        if (QbSystemFids.IsSystem(qb.Id))
            return pb is { IsSystem: true }
                ? new(qb, qb.Id, pb.TypeCode, QbFieldAction.System)
                : new(qb, null, null, QbFieldAction.Block, $"System field {qb.Id} has no matching PowerBase system field.");

        if (qb.ParentFid is { } parent)
        {
            var key = AddressKeyOf(qb.Label);
            return key is null
                ? new(qb, null, null, QbFieldAction.Block, $"Address part '{qb.Label}' is not a known address part.")
                : new(qb, parent, "Address", QbFieldAction.AddressPart) { CompositeFid = parent, AddressKey = key };
        }

        if (qb.IsComputed)
            return new(qb, pb?.Fid, pb?.TypeCode, QbFieldAction.Computed,
                pb is null ? "Computed in QuickBase; no PowerBase field (not written either way)." : null);

        if (pb is null)
            return qb.FieldType.Equals("file", StringComparison.OrdinalIgnoreCase)
                ? new(qb, null, null, QbFieldAction.Skip, "File attachments move in the pilot stage.")
                : new(qb, null, null, QbFieldAction.Block, $"No PowerBase field with number {qb.Id}; the QBL import skipped or renumbered it.");

        if (pb.Description?.StartsWith(PlaceholderMarker, StringComparison.Ordinal) == true)
            return new(qb, pb.Fid, pb.TypeCode, QbFieldAction.Block, "PowerBase field is an import placeholder, not the real field.");
        if (pb.IsHidden)
            return new(qb, pb.Fid, pb.TypeCode, QbFieldAction.Skip, "PowerBase field is a hidden helper field.");
        if (PhysicalNaming.IsComputedTypeCode(pb.TypeCode))
            return new(qb, pb.Fid, pb.TypeCode, QbFieldAction.Block, $"Stored in QuickBase but computed ({pb.TypeCode}) in PowerBase.");
        if (PhysicalNaming.IsRangeTypeCode(pb.TypeCode))
            return new(qb, pb.Fid, pb.TypeCode, QbFieldAction.Block, $"{pb.TypeCode} has no QuickBase equivalent.");
        if (pb.TypeCode == "File")
            return new(qb, pb.Fid, pb.TypeCode, QbFieldAction.Skip, "File attachments move in the pilot stage.");
        if (!Compatible.TryGetValue(qb.FieldType, out var allowed) || !allowed.Contains(pb.TypeCode))
            return new(qb, pb.Fid, pb.TypeCode, QbFieldAction.Block, $"QuickBase type '{qb.FieldType}' can't be written into PowerBase {pb.TypeCode}.");

        var pbLabel = pb.Label ?? pb.Name;
        var reason = string.Equals(pbLabel.Trim(), qb.Label.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null : $"Label differs: QuickBase '{qb.Label}', PowerBase '{pbLabel}'.";
        return new(qb, pb.Fid, pb.TypeCode, QbFieldAction.Load, reason);
    }

    /// <summary>"Address: Street 1" gives street1. QuickBase names sub-fields after their composite field.</summary>
    public static string? AddressKeyOf(string label)
    {
        var idx = label.LastIndexOf(':');
        var part = idx >= 0 ? label[(idx + 1)..] : label;
        return AddressParts.KeyOf(part);
    }

    /// <summary>A table is safe to load only when no field is blocked.</summary>
    public static bool IsLoadable(IReadOnlyList<QbFieldMapping> mappings) => mappings.All(m => m.Action != QbFieldAction.Block);
}
