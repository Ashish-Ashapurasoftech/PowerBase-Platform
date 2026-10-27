using System.Globalization;
using System.Text.Json;
using PowerBase.Domain.Constants;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Pure SQL-building helpers for <see cref="FieldColumnConversionService"/> — separated so the naming and the
/// value-to-text rules are unit-testable without a database. Names are built from integer ids only; user text
/// never enters a SQL string (values travel as one JSON parameter read with OPENJSON).
/// </summary>
public static class FieldColumnConversionSql
{
    /// <summary>Name the old column is parked under while the new one is filled.</summary>
    public static string OldColumnName(int fid) => $"{PhysicalNaming.ColumnName(fid)}__conv_old";

    /// <summary>Canonical text for a converted value, which SQL Server CONVERTs into the target column type
    /// unambiguously (ISO dates, invariant numbers, 1/0 bits) whatever the session language.</summary>
    public static string? ToStorageText(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "1" : "0",
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>One batch of (row id, value) pairs as the JSON array the batched UPDATE reads.</summary>
    public static string BatchJson(IEnumerable<KeyValuePair<long, object?>> values) =>
        JsonSerializer.Serialize(values
            .Select(p => new { id = p.Key, v = ToStorageText(p.Value) })
            .Where(p => p.v is not null));

    /// <summary>Copies one JSON batch into <paramref name="column"/>, converting to its type. CONVERT (not TRY_CONVERT):
    /// the values were already converted in C#, so a failure here is a bug and must roll the whole change back.</summary>
    public static string BatchUpdateSql(string physicalTable, string column, string sqlDataType) => $"""
        UPDATE t SET {column} = CONVERT({sqlDataType}, m.v)
        FROM {physicalTable} t
        JOIN OPENJSON(@json) WITH (id BIGINT '$.id', v NVARCHAR(MAX) '$.v') m ON t.Id = m.id;
        """;

    /// <summary>Lists every non-PK index that includes the column, plus the Unique digest column SetUniqueAsync may
    /// have added for MAX columns — both must go before the column can be renamed away and dropped.</summary>
    public static string IndexesOnColumnSql => """
        SELECT DISTINCT i.name
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(@table) AND i.is_primary_key = 0 AND c.name IN (@column, @digestColumn);
        """;

    public static string UniqueDigestColumnName(int fid) => $"{PhysicalNaming.ColumnName(fid)}_unique_hash";
}
