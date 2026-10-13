namespace PowerBase.Formula.Binding;

/// <summary>
/// The by-ID spelling of a field reference: <c>[fid_14]</c>. The token may also carry a name
/// snapshot after a <c>|</c> (<c>[fid_14|Customer Name]</c>) — the part before the separator is
/// always what resolves; the snapshot is informational only.
/// </summary>
public static class FieldIdReference
{
    public const string Prefix = "fid_";
    public const char SnapshotSeparator = '|';

    /// <summary>Parses the field number out of <c>fid_14</c> or <c>fid_14|Snapshot</c>. Case-insensitive on the prefix.</summary>
    public static bool TryParse(string? token, out long fid)
    {
        fid = 0;
        if (token is null) return false;

        var span = token.AsSpan().Trim();
        if (!span.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        span = span[Prefix.Length..];
        var sep = span.IndexOf(SnapshotSeparator);
        var digits = sep >= 0 ? span[..sep] : span;
        if (digits.IsEmpty) return false;

        foreach (var c in digits)
            if (c is < '0' or > '9') return false;

        return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out fid);
    }

    /// <summary>True when a field label would be read as an ID reference (<c>fid_14</c>), so it can't be used as a name.</summary>
    public static bool IsReservedLabel(string? label) => TryParse(label, out _);
}
