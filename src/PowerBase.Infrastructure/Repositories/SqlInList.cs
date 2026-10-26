using System.Text.Json;
using Dapper;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>
/// <c>{column} IN (...)</c> over a key list of any length. Dapper expands <c>IN @list</c> into one parameter per item, and
/// SQL Server refuses a command with more than 2,100 parameters — e.g. a report sorted by a formula projects up to 50,000
/// rows, and every lookup, summary and user name of those rows is read by key.
/// </summary>
internal static class SqlInList
{
    /// <summary>Longest key list still sent as a Dapper list.</summary>
    internal const int MaxParameters = 1000;

    /// <summary><c>{column} IN (...)</c> over <paramref name="keys"/>, adding the parameter it reads as
    /// <paramref name="parameterName"/>. A list up to <see cref="MaxParameters"/> stays a Dapper list, exactly as before;
    /// a longer list of whole numbers (row Ids, the Record ID#s a reference stores, user ids) is sent as one JSON array
    /// read with OPENJSON. Any other long list is still a Dapper list.</summary>
    internal static string Predicate<T>(string column, string parameterName, IReadOnlyCollection<T> keys, DynamicParameters parameters)
        where T : notnull
    {
        if (keys.Count > MaxParameters && WholeNumbers(keys.Cast<object>()) is { } ids)
        {
            parameters.Add(parameterName, JsonSerializer.Serialize(ids));
            return $"{column} IN (SELECT k FROM OPENJSON(@{parameterName}) WITH (k BIGINT '$'))";
        }
        parameters.Add(parameterName, keys);
        return $"{column} IN @{parameterName}";
    }

    /// <summary><paramref name="keys"/> as whole numbers, or null when any isn't one.</summary>
    private static List<long>? WholeNumbers(IEnumerable<object> keys)
    {
        var ids = new List<long>();
        foreach (var key in keys)
        {
            switch (key)
            {
                case long l: ids.Add(l); break;
                case int i: ids.Add(i); break;
                case short s: ids.Add(s); break;
                case byte b: ids.Add(b); break;
                case decimal d when d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue: ids.Add((long)d); break;
                default: return null;
            }
        }
        return ids;
    }
}
