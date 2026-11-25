using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pipelines.Outlook;

/// <summary>Sends a pipeline email through a user's connected Microsoft Outlook account.</summary>
public interface IOutlookConnectionService
{
    /// <summary>
    /// Resolves (and refreshes when needed) the access token of <paramref name="connectionId"/>, which must belong to
    /// <paramref name="ownerUserId"/>, then sends the message through Microsoft Graph.
    /// </summary>
    Task SendAsync(Guid connectionId, long ownerUserId, PipelineEmailMessage message, CancellationToken ct);
}

/// <summary>Low-level Microsoft Graph mail transport (no token storage).</summary>
public interface IOutlookGraphMailSender
{
    Task SendAsync(string accessToken, PipelineEmailMessage message, CancellationToken ct);
}

/// <summary>Microsoft Graph delegated permissions offered by the "Connect new account" panel (Quickbase parity).</summary>
public static class OutlookPermissionCatalog
{
    public sealed record Permission(string Scope, string Title, string Description, bool Required);

    /// <summary>Always requested so the refresh token and profile are returned.</summary>
    public static readonly string[] BaseScopes = ["openid", "profile", "email", "offline_access"];

    public static readonly IReadOnlyList<Permission> All =
    [
        new("User.Read", "Sign in and read user profile", "Allows users to sign-in to the app, and allows the app to read the profile of signed-in users. It also allows the app to read basic company information of signed-in users.", true),
        new("Mail.Send", "Send mail as a user", "Allows the app to send mail as users in the organization.", true),
        new("Calendars.Read", "Read user calendars", "Allows the app to read events in user calendars.", true),
        new("Mail.Read.Shared", "Read user and shared mail", "Allows the app to read mail a user can access, including their own and shared mail.", true),
        new("Mail.Read", "Read user mail", "Allows the app to read the signed-in user's mailbox.", true),
        new("Mail.Send.Shared", "Send mail on behalf of others", "Allows the app to send mail as the signed-in user, including sending on-behalf of others.", true),
        // Needed for attachments over 3 MB: Graph only accepts them through a draft + upload session.
        new("Mail.ReadWrite", "Read and write user mail", "Allows the app to create, read, update, and delete email in the signed-in user's mailbox. Needed to send attachments larger than 3 MB.", true),
        new("Calendars.ReadWrite", "Have full access to user calendars", "Allows the app to create, read, update, and delete events in user calendars.", false),
        new("Mail.ReadWrite.Shared", "Read and write user and shared mail", "Allows the app to create, read, update, and delete mail that the user has permission to access, including the user's own and shared mail. Does not include permission to send mail.", false),
        new("Calendars.Read.Shared", "Read user and shared calendars", "Allows the app to read events in all calendars that the user can access, including delegate and shared calendars.", false),
        new("Calendars.ReadWrite.Shared", "Read and write user and shared calendars", "Allows the app to read, update, create and delete events in all calendars in the organization user has permissions to access. This includes delegate and shared calendars.", false),
    ];

    /// <summary>Required + selected optional scopes; unknown scopes are rejected.</summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string>? optionalScopes)
    {
        var selected = new HashSet<string>(optionalScopes ?? [], StringComparer.OrdinalIgnoreCase);
        var unknown = selected.Where(scope => !All.Any(p => !p.Required && string.Equals(p.Scope, scope, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0) throw new ArgumentException("Unknown Outlook permission: " + string.Join(", ", unknown));
        return BaseScopes
            .Concat(All.Where(p => p.Required || selected.Contains(p.Scope)).Select(p => p.Scope))
            .ToList();
    }
}
