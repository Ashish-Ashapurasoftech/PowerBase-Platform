using System.Text.RegularExpressions;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Recovers from a specific, confirmed-real corruption pattern in QBL exports: a
/// <c>QB::CodePage</c>'s <c>RawCode</c> literal block (minified JS/HTML) can contain a line
/// with less indentation than the block requires, which prematurely ends the YAML block scalar
/// and breaks strict parsing ("While parsing a block mapping, did not find expected key").
///
/// <c>QB::CodePage</c> — and the rest of the Application-level <c>Pages</c> collection it lives
/// in (dashboards, rich-text pages) — has no PowerBase equivalent regardless of whether it
/// parses, so excising that whole block on a parse failure loses nothing importable. This is a
/// recovery path, not the primary parse route: <see cref="QblSerializer"/> only calls it after
/// a normal parse attempt has already failed.
/// </summary>
internal static class QblRawTextSanitizer
{
    private static readonly Regex BareYamlKeyPattern = new(@"^[A-Z][A-Za-z0-9_]*:\s*$", RegexOptions.Compiled);


    /// <summary>Returns the sanitized text with the Application-level <c>Pages:</c> block
    /// removed, or null if that block can't be safely located (nothing to recover with).</summary>
    public static string? TryStripUnsupportedPagesBlock(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');

        // "Tables:" always exists at the Application node's own child indentation (it's
        // required for a valid app) — use it as the reference indent for "Pages:"'s sibling
        // level, rather than assuming a fixed column count.
        var appChildIndent = FindKeyIndent(lines, "Tables:");
        if (appChildIndent is null)
            return null;

        var pagesLineIndex = FindLineIndex(lines, "Pages:", appChildIndent.Value);
        if (pagesLineIndex is null)
            return null;

        // The corrupted RawCode content itself can contain lines with little or no leading
        // whitespace (raw minified JS, not YAML structure) — a naive "first line with indent
        // <= appChildIndent" boundary check gets fooled by those and stops mid-corruption.
        // Requiring the dedented line to actually *look like* a bare YAML key (a capitalized
        // identifier followed by a colon and nothing else — the shape of every real sibling key
        // here: Tables/Roles/Pages/Variables) reliably tells a real boundary apart from stray
        // corrupted content, which won't have that shape.
        var endIndex = lines.Length;
        for (var i = pagesLineIndex.Value + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var indent = lines[i].Length - lines[i].TrimStart(' ').Length;
            if (indent > appChildIndent.Value)
                continue;

            if (BareYamlKeyPattern.IsMatch(lines[i][indent..]))
            {
                endIndex = i;
                break;
            }
        }

        var result = new List<string>(lines.Length);
        result.AddRange(lines[..pagesLineIndex.Value]);
        result.AddRange(lines[endIndex..]);
        return string.Join('\n', result);
    }

    private static int? FindKeyIndent(string[] lines, string key)
    {
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart(' ');
            if (trimmed == key)
                return line.Length - trimmed.Length;
        }
        return null;
    }

    private static int? FindLineIndex(string[] lines, string key, int expectedIndent)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart(' ');
            if (trimmed != key)
                continue;

            var indent = lines[i].Length - trimmed.Length;
            if (indent == expectedIndent)
                return i;
        }
        return null;
    }
}
