using System.Data;
using System.Text.RegularExpressions;
using Dapper;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// SQL types of stored c_ columns (design 4.3). A Lookup copies its source column's type; a Min/Max summary its target's; Count is INT,
/// Exists BIT, Sum DECIMAL(38,4) and Avg DECIMAL(38,6) (the exact types SUM / AVG over DECIMAL(18,4) return, so nothing is rounded).
/// Text of any length becomes an NVARCHAR(400) sort key: sorting and "starts with" use the first 400 characters.
/// </summary>
public static class SqlColumnType
{
    public const string TextSortKey = "NVARCHAR(400)";
    public const int TextSortKeyLength = 400;

    private static readonly Regex Allowed = new(
        @"^(BIT|TINYINT|SMALLINT|INT|BIGINT|DATE|NVARCHAR\(400\)|DECIMAL\(\d{1,2},\d{1,2}\)|DATETIME2\(\d\)|TIME\(\d\))$", RegexOptions.Compiled);

    /// <summary>True for the only types the schema engine will add as a c_ column (the type string is built from integers we control).</summary>
    public static bool IsAllowed(string sqlType) => Allowed.IsMatch(sqlType);

    public static bool IsText(string sqlType) => sqlType == TextSortKey;

    /// <summary>"DECIMAL(18,4)", "NVARCHAR(MAX)", "DATETIME2(3)"… from sys.columns.</summary>
    public static string Format(string typeName, short maxLength, byte precision, byte scale) => typeName.ToLowerInvariant() switch
    {
        "nvarchar" or "nchar" => maxLength == -1 ? "NVARCHAR(MAX)" : $"NVARCHAR({maxLength / 2})",
        "varchar" or "char" => maxLength == -1 ? "VARCHAR(MAX)" : $"VARCHAR({maxLength})",
        "decimal" or "numeric" => $"DECIMAL({precision},{scale})",
        "datetime2" => $"DATETIME2({scale})",
        "time" => $"TIME({scale})",
        var other => other.ToUpperInvariant()
    };

    /// <summary>The stored type for a value read from a column of <paramref name="sourceType"/>; null when it is not storable.</summary>
    public static string? StoredTypeFor(string? sourceType)
    {
        if (sourceType is null) return null;
        if (sourceType.StartsWith("NVARCHAR", StringComparison.Ordinal) || sourceType.StartsWith("VARCHAR", StringComparison.Ordinal)
            || sourceType is "NTEXT" or "TEXT" || sourceType.StartsWith("NCHAR", StringComparison.Ordinal) || sourceType.StartsWith("CHAR", StringComparison.Ordinal))
            return TextSortKey;
        if (sourceType == "DATETIME") return "DATETIME2(3)";
        return IsAllowed(sourceType) ? sourceType : null;
    }

    /// <summary>The value expression cast to the stored type (text cut to the sort-key length).</summary>
    public static string CastTo(string valueExpr, string sqlType) =>
        IsText(sqlType)
            ? $"CAST(LEFT(CAST({valueExpr} AS NVARCHAR(MAX)), {TextSortKeyLength}) AS {TextSortKey})"
            : $"CAST({valueExpr} AS {sqlType})";

    private const string ReadSql = """
        SELECT TYPE_NAME(c.user_type_id) AS TypeName, c.max_length AS MaxLength, c.precision AS [Precision], c.scale AS Scale
        FROM sys.columns c
        WHERE c.object_id = OBJECT_ID(@table) AND c.name = @column
        """;

    /// <summary>The column's type, or null when the column does not exist.</summary>
    public static async Task<string?> ReadAsync(IDbConnection connection, string fullTableName, string column, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        var row = await connection.QuerySingleOrDefaultAsync<(string TypeName, short MaxLength, byte Precision, byte Scale)?>(
            new CommandDefinition(ReadSql, new { table = fullTableName, column }, tx, cancellationToken: ct));
        return row is { } r ? Format(r.TypeName, r.MaxLength, r.Precision, r.Scale) : null;
    }
}
