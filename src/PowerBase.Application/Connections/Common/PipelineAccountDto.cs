using System;

namespace PowerBase.Application.Connections.Common;

/// <summary>
/// Display-safe projection of a saved PowerFlows account.
/// Deliberately carries no internal Id, no TokenHash and no raw token — the raw token
/// is never persisted and is never returned by any endpoint after it is supplied.
/// </summary>
public class PipelineAccountDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AuthMode { get; set; } = string.Empty;
    public string? Subdomain { get; set; }

    /// <summary>Masked prefix for display only, e.g. <c>pb_ut_abc…</c>.</summary>
    public string? TokenPrefix { get; set; }

    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
