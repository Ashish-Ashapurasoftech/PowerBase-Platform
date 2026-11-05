namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>Parses optional query-string values the way Quickbase validates them (400 on a malformed value).</summary>
public static class QbQueryParams
{
    public static bool? ParseBool(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value.Trim(), out var b)) return b;
        throw QbApiException.BadRequest($"'{name}' must be boolean");
    }

    public static int? ParseNonNegativeInt(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value.Trim(), out var n)) throw QbApiException.BadRequest($"'{name}' must be integer");
        if (n < 0)
            throw QbApiException.InvalidRequest(name == "top"
                ? "Report parameter 'top' must be greater than zero."
                : $"Report parameter '{name}' must be greater than or equal to zero.");
        return n;
    }
}
