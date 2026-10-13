using System.Globalization;

namespace PowerBase.Formula.Binding;

/// <summary>
/// The by-ID spelling of an app-variable reference: <c>[var_5]</c>, optionally with a name snapshot after a
/// <c>|</c> (<c>[var_5|Tax]</c>) — the part before the separator is what resolves. The counterpart of
/// <see cref="FieldIdReference"/> (<c>[fid_14]</c>) for variables.
/// </summary>
public static class VariableIdReference
{
    public const string Prefix = "var_";
    public const char SnapshotSeparator = '|';

    /// <summary>Parses the variable id out of <c>var_5</c> or <c>var_5|Snapshot</c>. Case-insensitive on the prefix.</summary>
    public static bool TryParse(string? token, out long id)
    {
        id = 0;
        if (token is null) return false;

        var span = token.AsSpan().Trim();
        if (!span.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        span = span[Prefix.Length..];
        var sep = span.IndexOf(SnapshotSeparator);
        var digits = sep >= 0 ? span[..sep] : span;
        if (digits.IsEmpty) return false;

        foreach (var c in digits)
            if (c is < '0' or > '9') return false;

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>True when a variable name would be read as an ID reference (<c>var_5</c>), so it can't be used as a name.</summary>
    public static bool IsReservedName(string? name) => TryParse(name, out _);
}
