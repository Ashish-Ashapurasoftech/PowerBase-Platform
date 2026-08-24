using System;

namespace PowerBase.Application.Connections.Commands.CreateConnection;

/// <summary>
/// "Connect new account" → "Authenticate with user token".
///
/// The other authentication choice, "Authenticate with my user", has no command: it can only
/// ever resolve to a realm the user is already permitted to use, so the selector picks that
/// existing entry from <c>GET /pipelines/available-tenants</c> instead of creating a second
/// row for the same realm.
///
/// <see cref="Subdomain"/> is the company subdomain (realm slug) the user typed.
/// <see cref="UserToken"/> is the raw secret — hashed on arrival, never stored, never echoed back.
/// </summary>
public record CreateConnectionCommand(string AuthMode, string Subdomain, string UserToken, string? Name);
