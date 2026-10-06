namespace PowerBase.Application.Imports;

/// <summary>Decides which web address the link in a completion email points at. The request's Origin header comes from the
/// caller, so it is only trusted when it is a known frontend (a configured address, a CORS-allowed origin, or this machine);
/// otherwise the email carries no link rather than one an attacker could aim at their own site.</summary>
public static class ImportLinks
{
    public static string? TrustedBaseUrl(string? configured, string? origin, IEnumerable<string> allowedOrigins)
    {
        if (Normalize(configured) is { } fromConfig) return fromConfig;
        var candidate = Normalize(origin);
        if (candidate is null) return null;
        var uri = new Uri(candidate);
        var known = uri.IsLoopback || allowedOrigins.Select(Normalize).Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase));
        return known ? candidate : null;
    }

    /// <summary>"scheme://host[:port]" for an http(s) address, without any path; null for anything else.</summary>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
