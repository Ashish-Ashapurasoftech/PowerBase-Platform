using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PowerBase.Application.Search;

/// <summary>
/// How a field's value reads for quick search and global search: the text a user would type to find it. One rule for the index's
/// free-text field and for every check made without the index (a record the index has not caught up with, a global search result
/// checked against the fields its viewer may see), so they always agree.
/// </summary>
public static class SearchText
{
    /// <summary>The searchable text of one value, or null when it has none. Numbers read as shown (no trailing zeros), dates as
    /// yyyy-MM-dd (with the time when there is one). A yes/no or a range has no text to search.</summary>
    public static string? Format(string? typeCode, object? value)
    {
        if (value is null || value is DBNull) return null;
        if (SearchSlotResolver.IsRange(typeCode) || SearchSlotResolver.IsBoolean(typeCode)) return null;

        if (SearchSlotResolver.IsNumeric(typeCode))
        {
            if (value is decimal dec) return dec.ToString("0.############################", CultureInfo.InvariantCulture);
            var d = SearchSlotResolver.ParseDouble(value);
            return d?.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        if (SearchSlotResolver.IsDate(typeCode))
        {
            var dto = SearchSlotResolver.ParseDateTimeOffset(value);
            if (dto == null) return null;
            return dto.Value.TimeOfDay == TimeSpan.Zero
                ? dto.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dto.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        var text = SearchSlotResolver.ParseString(value).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The free-text of a record: every searchable field's text, in field order, separated by spaces (empty when none has any).</summary>
    public static string Join(IEnumerable<(long Fid, string? TypeCode)> searchableFields, IReadOnlyDictionary<long, object?> values) =>
        string.Join(" ", searchableFields
            .Select(f => values.TryGetValue(f.Fid, out var v) ? Format(f.TypeCode, v) : null)
            .Where(t => !string.IsNullOrEmpty(t)));

    private static readonly Regex Special = new(@"[\+\-\&\|\!\(\)\{\}\[\]\^\""\~\*\?\:\\\/]", RegexOptions.Compiled);

    /// <summary>
    /// Whether <paramref name="texts"/> match a search the way the index matches its free-text field (see
    /// AzureSearchService.FormatSearchQuery): every word of the search must start a word of the text (a word of one or two letters must
    /// be a whole word), in any field; a search in quotes must appear as written. Letter case does not matter.
    /// </summary>
    public static bool Matches(string search, IEnumerable<string?> texts)
    {
        var haystack = string.Join(" \n ", texts.Where(t => !string.IsNullOrEmpty(t)));
        if (haystack.Length == 0 || string.IsNullOrWhiteSpace(search)) return false;

        var trimmed = search.Trim();
        if (trimmed.Length > 1 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
        {
            var phrase = trimmed[1..^1].Trim();
            return phrase.Length > 0 && WordAt(haystack, phrase, wholeWord: false);
        }

        var terms = Special.Replace(trimmed, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true; // the index answers "*" (everything) to a search with nothing left in it
        return terms.All(t => WordAt(haystack, t, wholeWord: t.Length < 3));
    }

    private static bool WordAt(string haystack, string term, bool wholeWord)
    {
        var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(term) + (wholeWord ? @"(?![\p{L}\p{N}])" : string.Empty);
        return Regex.IsMatch(haystack, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
