using System.Text.Json;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Entities;

namespace PowerBase.QbMigration.Tests;

internal static class TestData
{
    public static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static QbRow Row(long id, params (int Fid, string Json)[] values)
    {
        var dict = values.ToDictionary(v => v.Fid, v => J(v.Json));
        dict[QbSystemFids.RecordId] = J(id.ToString());
        return new QbRow(dict);
    }

    public static AppField Pb(int fid, string typeCode, string? label = null, bool system = false) => new()
    {
        Id = fid, Fid = fid, TypeCode = typeCode, Name = label ?? $"F{fid}", Label = label ?? $"F{fid}", IsSystem = system,
        PhysicalColumnName = system ? fid switch { 1 => "CreatedOn", 2 => "ModifiedOn", 3 => "Id", 4 => "CreatedBy", _ => "ModifiedBy" } : null,
    };

    public static List<AppField> SystemFields() =>
    [
        Pb(1, "DateTime", "Date Created", true), Pb(2, "DateTime", "Date Modified", true), Pb(3, "Number", "Record ID#", true),
        Pb(4, "User", "Record Owner", true), Pb(5, "User", "Last Modified By", true),
    ];

    public static List<QbField> QbSystemFields() =>
    [
        new(1, "Date Created", "timestamp", null, []), new(2, "Date Modified", "timestamp", null, []),
        new(3, "Record ID#", "recordid", null, []), new(4, "Record Owner", "user", null, []), new(5, "Last Modified By", "user", null, []),
    ];

    public sealed class Lookups(Dictionary<string, long>? users = null, Dictionary<(int, string), long>? refs = null) : IQbValueLookups
    {
        public long? UserId(string email) => users is not null && users.TryGetValue(email, out var id) ? id : null;
        public long? ReferenceId(int pbFid, string key) => refs is not null && refs.TryGetValue((pbFid, key), out var id) ? id : null;
    }
}
