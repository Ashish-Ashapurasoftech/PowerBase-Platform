using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>Turns a parent record as a client names it into its row Id: the row Id (Record ID#) of a live record, its
/// PublicId, or the key text a Reference reads back as when its parent shows a key — in the order the save check
/// tries them (<see cref="ReferenceWriteValidator"/>). 0 when nothing matches.</summary>
public static class ParentRecordResolver
{
    public static async Task<long> ResolveRowIdAsync(
        Relationship rel, AppTable parent, string parentRecord,
        IRecordRepository recordRepo, IAppFieldRepository fieldRepo, CancellationToken ct)
    {
        if (long.TryParse(parentRecord, out var rowId) && rowId > 0 && await recordRepo.ExistsAsync(parent, rowId, ct))
            return rowId;
        if (Guid.TryParse(parentRecord, out var publicId))
            return await recordRepo.GetRecordIdByPublicIdAsync(parent, publicId, ct: ct);
        if (string.IsNullOrWhiteSpace(parentRecord)) return 0;

        // A Reference whose parent shows a key reads back as that key's text (e.g. "IT"), not the row Id.
        var displayKey = KeyFieldResolver.ResolveDisplayKey(rel, parent, await fieldRepo.ListByTableAsync(parent.Id, ct));
        if (KeyFieldResolver.ConvertToColumnType(displayKey, parentRecord.Trim()) is not { } typed) return 0;
        var matches = await recordRepo.GetIdsByColumnValuesAsync(parent, KeyFieldResolver.ColumnName(displayKey), [typed], ct);
        return matches.Count > 0 ? matches.Values.First() : 0;
    }
}
