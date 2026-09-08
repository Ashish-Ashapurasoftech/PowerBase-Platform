using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Records;

/// <summary>
/// Rewrites every User/MultiUser field's incoming value from the record form's picker wire format
/// (userPublicId Guid string, or a JSON array / comma list of them for MultiUser) into the plain
/// long core.[User].Id the physical column actually stores — see
/// RunReportQueryHandler.ResolveUserFieldValuesAsync's doc comment for the full chain of evidence
/// for why. Without this, a value picked through the normal Add/Edit Record form's picker was
/// written as a Guid, so it could never be matched again by an "is equal to"/"is the current user"
/// report filter (which correctly expects the long id) — confirmed by testing a freshly-picked
/// value against exactly that filter. A value that isn't a parseable Guid (already numeric, or
/// free text) passes through unchanged, so already-correct data is never touched.
///
/// Shared by CreateRecordCommandHandler (record creation, including "Specific User" default
/// values) and RecordWriteService (record update + Action Button writes), so every write path
/// resolves identically instead of each reimplementing the same lookup.
/// </summary>
public static class UserFieldValueResolver
{
    /// <summary>Looks up a picked user's real internal long id from their public Guid. Returns
    /// null (fail-safe) rather than throwing when the Guid doesn't match any user — a stale/
    /// deleted reference shouldn't block the whole record save.</summary>
    public static async Task<long?> TryResolveLongIdAsync(IUserRepository userRepo, Guid publicId, CancellationToken ct)
    {
        try { return (await userRepo.GetByPublicIdAsync(publicId, ct)).Id; }
        catch (Exception) { return null; }
    }

    /// <summary>Mutates effectiveValues in place, resolving every User/MultiUser field's value.</summary>
    public static async Task ResolveAsync(
        IUserRepository userRepo, IReadOnlyList<AppField> fields, Dictionary<long, object?> effectiveValues, CancellationToken ct)
    {
        var guidCache = new Dictionary<Guid, long>();
        foreach (var field in fields)
        {
            if (!field.Fid.HasValue || field.TypeCode is not ("User" or "MultiUser")) continue;
            if (!effectiveValues.TryGetValue(field.Fid.Value, out var raw) || raw is null) continue;

            var rawStr = raw is JsonElement je ? je.ToString() : raw.ToString();
            if (string.IsNullOrWhiteSpace(rawStr)) continue;

            var parts = rawStr.TrimStart('[').TrimEnd(']').Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().Trim('"'));
            var resolvedParts = new List<string>();
            var anyResolved = false;
            foreach (var part in parts)
            {
                if (Guid.TryParse(part, out var guid))
                {
                    if (!guidCache.TryGetValue(guid, out var longId))
                    {
                        var resolved = await TryResolveLongIdAsync(userRepo, guid, ct);
                        if (resolved is null) { resolvedParts.Add(part); continue; }
                        longId = resolved.Value;
                        guidCache[guid] = longId;
                    }
                    resolvedParts.Add(longId.ToString());
                    anyResolved = true;
                }
                else
                {
                    resolvedParts.Add(part);
                }
            }
            if (!anyResolved) continue;

            effectiveValues[field.Fid.Value] = field.TypeCode == "MultiUser"
                ? JsonSerializer.Serialize(resolvedParts)
                : resolvedParts[0];
        }
    }
}
