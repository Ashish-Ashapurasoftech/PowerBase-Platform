using System.Text;
using System.Text.RegularExpressions;

namespace PowerBase.Domain.Constants;

/// <summary>
/// Generates the stable, immutable Name for an AppField from its user-editable Label.
/// Name is exposed to third-party integrations as a stable field identifier — it is never
/// user-supplied and never changes after creation (see AppField.Name vs AppField.Label).
/// </summary>
public static class FieldNaming
{
    private const string CustomPrefix = "C_";
    private const string SystemPrefix = "S_";

    // AppField.Name is NVARCHAR(200) — leave headroom for the 2-char prefix and any
    // numeric collision suffix the caller may append (see IFieldNameResolver).
    private const int MaxSlugLength = 190;

    private static readonly Regex WordPattern = new("[A-Za-z0-9]+", RegexOptions.Compiled);

    /// <summary>Builds the base (pre-uniqueness) Name for a field, e.g. "Full Name" -> "C_fullName".</summary>
    public static string GenerateBaseName(string label, bool isSystem)
    {
        var slug = ToCamelSlug(label);
        var prefix = isSystem ? SystemPrefix : CustomPrefix;
        return prefix + slug;
    }

    private static string ToCamelSlug(string label)
    {
        var matches = WordPattern.Matches(label ?? string.Empty);
        if (matches.Count == 0)
            return "field";

        var sb = new StringBuilder();
        for (var i = 0; i < matches.Count; i++)
        {
            var word = matches[i].Value;
            if (i == 0)
            {
                sb.Append(word.ToLowerInvariant());
            }
            else
            {
                sb.Append(char.ToUpperInvariant(word[0]));
                if (word.Length > 1)
                    sb.Append(word[1..].ToLowerInvariant());
            }

            if (sb.Length >= MaxSlugLength)
                break;
        }

        var slug = sb.ToString();
        if (slug.Length > MaxSlugLength)
            slug = slug[..MaxSlugLength];

        // Identifiers shouldn't start with a digit.
        if (slug.Length > 0 && char.IsDigit(slug[0]))
            slug = "f" + slug;

        return slug.Length == 0 ? "field" : slug;
    }
}
