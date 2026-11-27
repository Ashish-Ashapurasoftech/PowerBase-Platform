using System.Globalization;
using System.Text.Json;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.V1.Services;

/// <summary>
/// Builds Quickbase user values (<c>{"email","id","name","userName"}</c>) for report rows. Our report handler resolves user
/// columns to display names only, so for stored User / MultiUser fields and the system Record Owner / Last Modified By
/// fields this reads the stored user ids for the returned rows and looks the users up. Nothing existing is changed.
/// </summary>
public sealed class QbUserValueResolver
{
    private readonly IRecordRepository _records;
    private readonly IUserRepository _users;

    private readonly PowerBase.Application.Apps.Queries.ListAppUsers.ListAppUsersQueryHandler? _appUsers;
    private readonly Dictionary<Guid, IReadOnlyList<PowerBase.Application.Apps.Queries.ListAppUsers.AppUserResult>> _appUserCache = new();

    public QbUserValueResolver(IRecordRepository records, IUserRepository users,
        PowerBase.Application.Apps.Queries.ListAppUsers.ListAppUsersQueryHandler? appUsers = null)
    {
        _records = records;
        _users = users;
        _appUsers = appUsers;
    }

    /// <summary>
    /// Formula and lookup user fields: our engine hands back only the user's display name (or e-mail), while Quickbase gives the
    /// whole user object (live realm: PX User / Parent - P User = {"email","id","name","userName"}). Each such value is matched
    /// against the app's users by name or e-mail and filled in only when exactly one user matches; otherwise it stays as it is.
    /// </summary>
    public async Task EnrichNamedUsersAsync(Guid appPublicId, IEnumerable<IDictionary<string, QbCell>> rows, IReadOnlyCollection<string> keys,
        CancellationToken ct)
    {
        if (keys.Count == 0 || _appUsers is null) return;
        if (!_appUserCache.TryGetValue(appPublicId, out var users))
        {
            try
            {
                users = (await _appUsers.HandleAsync(new PowerBase.Application.Apps.Queries.ListAppUsers.ListAppUsersQuery(
                    appPublicId, 1, int.MaxValue, null, "userName", false, IsExport: true), ct)).Items;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                users = Array.Empty<PowerBase.Application.Apps.Queries.ListAppUsers.AppUserResult>();
            }
            _appUserCache[appPublicId] = users;
        }
        if (users.Count == 0) return;
        var distinct = users.GroupBy(u => u.UserPublicId).Select(g => g.First()).ToList();
        object? Fill(object? v)
        {
            // A file value: each version's creator is a user object too (live realm: email / id / name / userName).
            if (v is IDictionary<string, object?> file && file.TryGetValue("versions", out var vs) && vs is System.Collections.IList versions)
            {
                foreach (var ver in versions)
                    if (ver is IDictionary<string, object?> vd && vd.TryGetValue("creator", out var creator) && creator is IDictionary<string, object?>)
                        vd["creator"] = Fill(creator);
                return v;
            }
            if (v is not IDictionary<string, object?> map || (map.TryGetValue("id", out var id) && id is not null)) return v;
            var name = map.TryGetValue("name", out var n) ? n as string : null;
            var email = map.TryGetValue("email", out var em) ? em as string : null;
            var hits = distinct.Where(u =>
                (!string.IsNullOrWhiteSpace(email) && string.Equals(u.UserEmail, email, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(name) && (string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(u.UserEmail, name, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (hits.Count != 1) return v;
            var u = hits[0];
            return new Dictionary<string, object?>
            {
                ["email"] = u.UserEmail, ["id"] = QbIds.Format(u.UserPublicId), ["name"] = u.UserName, ["userName"] = u.UserEmail,
            };
        }
        foreach (var row in rows)
            foreach (var key in keys)
            {
                if (!row.TryGetValue(key, out var cell) || cell?.Value is null) continue;
                row[key] = cell.Value is System.Collections.IList list
                    ? new QbCell(list.Cast<object?>().Select(Fill).ToList())
                    : new QbCell(Fill(cell.Value));
            }
    }

    /// <summary>True when the field's stored column holds user ids we can resolve.</summary>
    public static bool IsStoredUserField(AppField f) =>
        f.TypeCode is "User" or "MultiUser"
        || (f.IsSystem && f.PhysicalColumnName is "CreatedBy" or "ModifiedBy");

    public static string Column(AppField f) =>
        f.IsSystem && !string.IsNullOrEmpty(f.PhysicalColumnName) ? f.PhysicalColumnName! : PhysicalNaming.ColumnName(f.Fid!.Value);

    /// <summary>
    /// Returns record PublicId → fid → Quickbase user value (object for User, list for MultiUser) for the given fields.
    /// </summary>
    public async Task<Dictionary<Guid, Dictionary<long, object?>>> ResolveAsync(AppTable table, IReadOnlyList<AppField> userFields,
        IReadOnlyCollection<Guid> recordIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Dictionary<long, object?>>();
        if (userFields.Count == 0 || recordIds.Count == 0) return result;

        var idMap = await _records.GetIdsByPublicIdsMapAsync(table, recordIds, ct);
        var rows = await _records.GetRowsByIdsAsync(table, userFields, idMap.Values.ToList(), ct);
        var byRid = idMap.ToDictionary(kv => kv.Value, kv => kv.Key);

        var raw = new List<(Guid Record, AppField Field, List<long> Users)>();
        foreach (var (rid, row) in rows)
        {
            if (!byRid.TryGetValue(rid, out var publicId)) continue;
            foreach (var f in userFields)
            {
                var ids = row.TryGetValue(Column(f), out var v) ? ParseIds(v) : new List<long>();
                // Quickbase's Last Modified By is never empty: a record never edited was last modified by its creator.
                if (ids.Count == 0 && f.IsSystem && f.PhysicalColumnName == "ModifiedBy" && row.TryGetValue("CreatedBy", out var by))
                    ids = ParseIds(by);
                raw.Add((publicId, f, ids));
            }
        }

        var allIds = raw.SelectMany(r => r.Users).Distinct().ToList();
        var identities = allIds.Count == 0 ? new Dictionary<long, UserIdentity>() : await _users.GetIdentitiesByIdsAsync(allIds, ct);
        var publicIds = allIds.Count == 0 ? new Dictionary<long, Guid>() : await _users.GetPublicIdsByIdsAsync(allIds, ct);

        foreach (var (record, field, users) in raw)
        {
            if (!result.TryGetValue(record, out var cells)) result[record] = cells = new Dictionary<long, object?>();
            var objects = users.Select(id => UserObject(id, identities, publicIds)).ToList();
            cells[field.Fid!.Value] = field.TypeCode == "MultiUser"
                ? objects                    // Quickbase: an empty multi-user field is []
                : objects.FirstOrDefault();
        }
        return result;
    }

    public static Dictionary<string, object?> UserObject(long id, IReadOnlyDictionary<long, UserIdentity> identities, IReadOnlyDictionary<long, Guid> publicIds)
    {
        identities.TryGetValue(id, out var identity);
        return new Dictionary<string, object?>
        {
            ["email"] = identity?.Email,
            ["id"] = publicIds.TryGetValue(id, out var g) ? QbIds.Format(g) : null,
            ["name"] = identity?.Name,
            // Quickbase's sign-in name; ours is the e-mail address.
            ["userName"] = identity?.Email,
        };
    }

    /// <summary>User PublicIds in an upsert value: one Guid string or a JSON array of Guid strings.</summary>
    public static List<Guid> ParseGuids(object? value)
    {
        var s = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return new();
        if (s.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                return doc.RootElement.EnumerateArray().Select(e => Guid.TryParse(e.ToString(), out var g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty).ToList();
            }
            catch (JsonException) { return new(); }
        }
        return Guid.TryParse(s, out var one) ? new() { one } : new();
    }

    /// <summary>Stored user values: a single id, a JSON array of ids, or a comma/semicolon list.</summary>
    public static List<long> ParseIds(object? value)
    {
        if (value is null || value is DBNull) return new();
        switch (value)
        {
            case long l: return new() { l };
            case int i: return new() { i };
            case decimal d: return new() { (long)d };
        }
        var s = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return new();
        if (s.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.Number ? e.GetInt64()
                        : long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0)
                    .Where(x => x > 0).ToList();
            }
            catch (JsonException) { return new(); }
        }
        return s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0)
            .Where(x => x > 0).ToList();
    }
}
