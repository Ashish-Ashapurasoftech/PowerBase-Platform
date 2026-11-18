using System.Text.RegularExpressions;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Guards for what the migration sends out or keeps: the PowerBase API the super admin token may go to, and email
/// addresses in report rows.</summary>
public static class QbmSafety
{
    /// <summary>The PowerBase API the migration reindexes through. Other hosts must be listed in PowerBase:AllowedApiHosts.</summary>
    public const string DefaultApiHost = "powerbaseapi-wus2-fdemhzg8fycse9hx.westus2-01.azurewebsites.net";

    /// <summary>The API url as a Uri when it is https and its host is allowed; otherwise throws, before any token is sent.</summary>
    public static Uri ApiUri(string url, string? allowedHosts)
    {
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"'{url}' is not a valid PowerBase API url.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The PowerBase API url must use https; the super admin token is never sent over http.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("The PowerBase API url must not contain a user name, query or fragment.");
        var hosts = (allowedHosts ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Append(DefaultApiHost);
        if (!hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Host '{uri.IdnHost}' is not an allowed PowerBase API host. Add it to PowerBase:AllowedApiHosts if it is one.");
        return uri;
    }

    // Anything without spaces, quotes or brackets around an @ (any script, any characters QuickBase allows): over-matching only
    // masks more, never less.
    private static readonly Regex Email = new(@"([^\s@<>""(),;:\[\]]+)@([^\s@<>""'(),;:\[\]]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Email addresses in <paramref name="text"/> masked to their first two characters and domain (ch***@x.com).</summary>
    public static string MaskEmails(string text) =>
        Email.Replace(text, m => (m.Groups[1].Value.Length <= 2 ? m.Groups[1].Value[..1] : m.Groups[1].Value[..2]) + "***@" + m.Groups[2].Value.TrimEnd('.'));
}
