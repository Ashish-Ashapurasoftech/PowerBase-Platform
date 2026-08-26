using System;
using System.Collections.Generic;

namespace PowerBase.Application.Connections.Common;

/// <summary>
/// The fully-verified execution context behind a saved PowerFlows account.
///
/// Three identities are involved and must not be conflated:
///   L — the logged-in user making the request (owns the meta.PipelineAccount row)
///   T — the user the supplied token belongs to (<see cref="TargetUserId"/>)
///   X — the tenant the account grants access to (<see cref="TargetTenantId"/>)
///
/// A scope is only produced when BOTH gates pass: L owns the row, and T still holds a
/// live token and is an active member of X. Either gate failing is an explicit error —
/// never a silent fall back to L's own tenant.
/// </summary>
public sealed class ConnectionScope
{
    public required long AccountId { get; init; }
    public required Guid ConnectionPublicId { get; init; }
    public required long TargetTenantId { get; init; }
    public required long TargetUserId { get; init; }

    /// <summary>True when the account is backed by a user token, so token app restrictions apply.</summary>
    public required bool IsUserToken { get; init; }

    public required bool TokenAccessAllApps { get; init; }
    public required IReadOnlySet<long> AllowedAppIds { get; init; }
}
