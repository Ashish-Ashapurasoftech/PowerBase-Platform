using System.Text;

namespace PowerBase.Domain.Constants;

/// <summary>
/// Generates a table's stable formula alias — <c>_DBID_{TABLE_NAME}</c> — from its display name.
/// Applied once at table creation (see <c>CreateTableCommandHandler</c>); the result is persisted
/// on <see cref="Entities.AppTable.Alias"/> and never regenerated on rename, so existing Custom
/// Data Rules referencing it keep working.
/// </summary>
public static class TableAliasNaming
{
    private const string Prefix = "_DBID_";

    /// <summary>Slugs <paramref name="tableName"/> into the alias body: uppercase, runs of
    /// non-alphanumeric characters collapsed to a single underscore, leading/trailing underscores
    /// trimmed. "File Types" → "_DBID_FILE_TYPES". Never returns just "_DBID_" for a non-blank
    /// input — a name with no alphanumeric characters at all falls back to "TABLE".</summary>
    public static string Generate(string tableName)
    {
        var sb = new StringBuilder(tableName.Length);
        var lastWasUnderscore = false;
        foreach (var c in tableName)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore && sb.Length > 0)
            {
                sb.Append('_');
                lastWasUnderscore = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == '_') sb.Length--;

        var body = sb.Length > 0 ? sb.ToString() : "TABLE";
        return Prefix + body;
    }

    /// <summary>True when <paramref name="text"/> is shaped like a table alias token
    /// (<c>_DBID_*</c>), used by the binder to decide whether an unresolved field-schema lookup
    /// should fall through to table-alias resolution instead of an "unknown field" diagnostic.</summary>
    public static bool LooksLikeAlias(string text) =>
        text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
