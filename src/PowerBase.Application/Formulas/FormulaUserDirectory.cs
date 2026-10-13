using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Resolves a user id to its email and display name for <c>UserToEmail</c>/<c>UserToName</c>. A record
/// stores only the id (core.[User].Id); the directory lives in the control DB, reached through
/// <see cref="IUserRepository"/>. The evaluator is synchronous, so a lookup blocks — same as the cross-table
/// reads — and results are cached for the directory's lifetime. <see cref="Prefetch"/> loads many ids in one
/// query so a page of rows costs one round trip rather than one per row.
/// </summary>
internal sealed class FormulaUserDirectory
{
    private readonly IUserRepository _users;
    private readonly Dictionary<long, UserRef?> _cache = new();

    public FormulaUserDirectory(IUserRepository users) => _users = users;

    /// <summary>Loads every not-yet-cached numeric id in one query. Non-numeric values are ignored.</summary>
    public void Prefetch(IEnumerable<object?> rawValues)
    {
        var missing = new HashSet<long>();
        foreach (var raw in rawValues)
            foreach (var part in Split(raw))
                if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && !_cache.ContainsKey(id))
                    missing.Add(id);
        Load(missing);
    }

    public UserRef? Lookup(string userId)
    {
        if (!long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        if (!_cache.ContainsKey(id)) Load(new[] { id });
        return _cache[id];
    }

    private void Load(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return;
        IReadOnlyDictionary<long, UserIdentity> found;
        try { found = _users.GetIdentitiesByIdsAsync(ids).GetAwaiter().GetResult(); }
        catch (Exception) { found = new Dictionary<long, UserIdentity>(); } // directory unavailable: fail soft to blank, once per id
        foreach (var id in ids)
            _cache[id] = found.TryGetValue(id, out var u)
                ? new UserRef(id.ToString(CultureInfo.InvariantCulture), u.Email, u.Name)
                : null;
    }

    // A User cell is one id; a MultiUser cell is a JSON id array ("[11,22]") or delimited text (see ValueConvert.ParseUserList).
    private static IEnumerable<string> Split(object? raw) => raw switch
    {
        null => [],
        string s => s.Trim().Trim('[', ']')
                     .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(p => p.Trim('"')),
        _ => [Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty],
    };
}
