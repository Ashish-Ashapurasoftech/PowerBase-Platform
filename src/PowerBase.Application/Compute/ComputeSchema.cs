using System.Collections.Generic;
using System.Linq;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Compute;

/// <summary>One stored-column node: a field on a table, by Fid. Fid <see cref="RowsFid"/> stands for "a row of the table was created,
/// deleted or restored" (a Summary counts rows, a Lookup blanks when its parent row is deleted).</summary>
public readonly record struct FieldKey(long TableId, int Fid)
{
    public const int RowsFid = 0;
    public static FieldKey Rows(long tableId) => new(tableId, RowsFid);
    public override string ToString() => $"t{TableId}.f{Fid}";
}

/// <summary>
/// The tenant's field metadata as the compute worker sees it: every live field of every live table, with each table's app and whether
/// that app is encrypted. Always read from the PRIMARY (metadata is never read from a replica).
/// </summary>
public sealed class ComputeSchema
{
    private readonly Dictionary<long, List<AppField>> _byTable;
    private readonly Dictionary<FieldKey, AppField> _byKey;
    private readonly IReadOnlyDictionary<long, long> _appByTable;
    private readonly IReadOnlySet<long> _encryptedApps;

    public ComputeSchema(IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, long> appByTable, IReadOnlySet<long> encryptedApps)
    {
        Fields = fields;
        _appByTable = appByTable;
        _encryptedApps = encryptedApps;
        _byTable = fields.GroupBy(f => f.AppTableId).ToDictionary(g => g.Key, g => g.ToList());
        _byKey = new Dictionary<FieldKey, AppField>();
        foreach (var f in fields)
            if (f.Fid is int fid) _byKey.TryAdd(new FieldKey(f.AppTableId, fid), f);
    }

    public IReadOnlyList<AppField> Fields { get; }

    public IReadOnlyList<AppField> FieldsOf(long tableId) => _byTable.TryGetValue(tableId, out var list) ? list : [];

    public AppField? Field(long tableId, int fid) => _byKey.GetValueOrDefault(new FieldKey(tableId, fid));

    public bool TableExists(long tableId) => _appByTable.ContainsKey(tableId);

    public bool IsAppEncrypted(long tableId) => _appByTable.TryGetValue(tableId, out var appId) && _encryptedApps.Contains(appId);

    public IReadOnlyDictionary<long, AppField> FieldsByFid(long tableId) =>
        FieldsOf(tableId).Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
}
