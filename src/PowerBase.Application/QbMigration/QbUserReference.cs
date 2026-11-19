using System.Text.Json;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.QbMigration;

// A QuickBase relationship can use a USER field as its foreign key (CFTSS "Related User Setting"): the child holds a user,
// the parent record is the one whose user field names that same user. The QBL import creates the PowerBase side as a plain
// Reference, which holds the parent's Record ID#. These types translate the user to that parent record, reading the
// parent's user field from QuickBase (so there is no load-order dependency, and users who have no PowerBase account
// still resolve). Nothing here writes to PowerBase or QuickBase.

/// <summary>Where the users of one child Reference field are looked up: the parent table and its user field, in QuickBase.</summary>
public sealed record QbUserReferenceSpec(string ParentQbTableId, int ParentKeyFid);

/// <summary>Parent records of one table by the user they name: QuickBase user id first, e-mail (any case) as a fallback.
/// A user named by several parent records resolves to the lowest Record ID#; <see cref="DuplicateUsers"/> counts them.</summary>
public sealed class QbUserParentIndex
{
    private readonly Dictionary<string, long> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _byEmail = new(StringComparer.OrdinalIgnoreCase);

    public int DuplicateUsers { get; private set; }

    public static async Task<QbUserParentIndex> BuildAsync(IQuickBaseClient client, QbUserReferenceSpec spec, CancellationToken ct = default)
    {
        var index = new QbUserParentIndex();
        // The keyset reader returns records in ascending Record ID#, so the first record seen for a user is the lowest.
        await foreach (var page in new QbKeysetReader(client).ReadAsync(spec.ParentQbTableId, [spec.ParentKeyFid], null, ct))
            foreach (var row in page)
                if (row.Values.TryGetValue(spec.ParentKeyFid, out var user)) index.Add(row.RecordId, user);
        return index;
    }

    private void Add(long recordId, JsonElement user)
    {
        var (id, email) = Describe(user);
        if (id is null && email is null) return;
        if ((id is not null && _byId.ContainsKey(id)) || (email is not null && _byEmail.ContainsKey(email)))
        {
            DuplicateUsers++;
            return;
        }
        if (id is not null) _byId[id] = recordId;
        if (email is not null) _byEmail[email] = recordId;
    }

    public long? Find(string? qbUserId, string? email)
    {
        if (!string.IsNullOrEmpty(qbUserId) && _byId.TryGetValue(qbUserId, out var byId)) return byId;
        if (!string.IsNullOrEmpty(email) && _byEmail.TryGetValue(email.Trim(), out var byEmail)) return byEmail;
        return null;
    }

    /// <summary>QuickBase sends a user as {email, id, name}; a bare string is taken as an e-mail.</summary>
    public static (string? Id, string? Email) Describe(JsonElement user)
    {
        switch (user.ValueKind)
        {
            case JsonValueKind.Object:
                var id = user.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                var email = user.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                return (string.IsNullOrWhiteSpace(id) ? null : id, string.IsNullOrWhiteSpace(email) ? null : email.Trim());
            case JsonValueKind.String when !string.IsNullOrWhiteSpace(user.GetString()):
                return (null, user.GetString()!.Trim());
            default:
                return (null, null);
        }
    }
}

/// <summary>Builds each parent index once per worker process and shares it between chunks (the parent table is small;
/// users added to it after the first chunk are picked up by the final sync, which runs in a new process).</summary>
public sealed class QbUserParentIndexes(IQuickBaseClient client)
{
    private readonly object _gate = new();
    private readonly Dictionary<QbUserReferenceSpec, Task<QbUserParentIndex>> _cache = [];

    public async Task<QbUserParentIndex> GetAsync(QbUserReferenceSpec spec)
    {
        Task<QbUserParentIndex> task;
        lock (_gate)
        {
            if (!_cache.TryGetValue(spec, out task!))
                // Shared by every chunk that needs it, so one caller's cancellation must not abandon it.
                _cache[spec] = task = QbUserParentIndex.BuildAsync(client, spec, CancellationToken.None);
        }
        try
        {
            return await task;
        }
        catch
        {
            // A failed read is not kept: the next chunk (or the retry of this one) reads the parent again.
            lock (_gate)
                if (_cache.TryGetValue(spec, out var cached) && ReferenceEquals(cached, task)) _cache.Remove(spec);
            throw;
        }
    }
}

/// <summary>Finds, for the user-typed Reference fields of one child table, the parent table and user field to match on.</summary>
public static class QbUserReferenceResolver
{
    /// <summary>The relationship's parent table as the run knows it: its QuickBase id (null when the run doesn't cover it) and PowerBase fields.</summary>
    public sealed record Parent(string? QbTableId, string Name, IReadOnlyList<AppField> Fields);

    public static bool IsUserReference(QbFieldMapping m) =>
        m.Action == QbFieldAction.Load && m.PbTypeCode == "Reference" && m.Qb.FieldType.Equals("user", StringComparison.OrdinalIgnoreCase);

    /// <param name="relations">(ReferenceFid, ParentTableId) of the child table's relationships.</param>
    /// <param name="parentOf">The parent table by PowerBase table id, or null when it isn't known.</param>
    public static (Dictionary<int, QbUserReferenceSpec> Specs, List<QbIssue> Problems) Resolve(
        IReadOnlyList<QbFieldMapping> mappings, IReadOnlyList<(int ReferenceFid, long ParentTableId)> relations, Func<long, Parent?> parentOf)
    {
        var specs = new Dictionary<int, QbUserReferenceSpec>();
        var problems = new List<QbIssue>();
        foreach (var m in mappings.Where(IsUserReference))
        {
            var fid = m.PbFid!.Value;
            var matching = relations.Where(r => r.ReferenceFid == fid).ToList();
            if (matching.Count == 0)
            {
                problems.Add(new("error", "QBM_USER_REFERENCE_NO_RELATIONSHIP",
                    $"'{m.Qb.Label}' ({m.Qb.Id}) is a QuickBase user field stored as a Reference, but PowerBase has no relationship for it; the users can't be linked.", null, m.Qb.Id));
                continue;
            }
            var parent = parentOf(matching[0].ParentTableId);
            if (parent?.QbTableId is null)
            {
                problems.Add(new("error", "QBM_USER_REFERENCE_PARENT_KEY",
                    $"'{m.Qb.Label}' ({m.Qb.Id}): its parent table isn't part of this run, so the users can't be matched to its records.", null, m.Qb.Id));
                continue;
            }
            var userFields = parent.Fields.Where(f => !f.IsDeleted && !f.IsSystem && !f.IsHidden && f.TypeCode == "User" && f.Fid.HasValue).OrderBy(f => f.Fid).ToList();
            if (userFields.Count != 1)
            {
                var found = userFields.Count == 0 ? "none" : string.Join(", ", userFields.Select(f => $"'{f.Label ?? f.Name}'"));
                problems.Add(new("error", "QBM_USER_REFERENCE_PARENT_KEY",
                    $"'{m.Qb.Label}' ({m.Qb.Id}): the users are matched on the one user field of '{parent.Name}', which has {userFields.Count} (found: {found}).", null, m.Qb.Id));
                continue;
            }
            specs[fid] = new QbUserReferenceSpec(parent.QbTableId, userFields[0].Fid!.Value);
        }
        return (specs, problems);
    }
}
