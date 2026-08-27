namespace PowerBase.API.Models.Connections;

/// <summary>
/// Body for <c>POST /pipelines/connections</c> — "Connect new account" with a user token.
///
/// <see cref="UserToken"/> is the raw secret. It is hashed on arrival and is never stored,
/// logged, or returned by any endpoint afterwards.
/// </summary>
public class CreatePipelineConnectionRequest
{
    /// <summary>Always <c>user_token</c>. "Authenticate with my user" selects an existing realm instead of creating an account.</summary>
    public string AuthMode { get; init; } = string.Empty;

    /// <summary>Company subdomain (realm slug) the account belongs to.</summary>
    public string Subdomain { get; init; } = string.Empty;

    public string UserToken { get; init; } = string.Empty;

    /// <summary>Optional display name. Defaults to "{token owner} ({subdomain})".</summary>
    public string? Name { get; init; }
}
