using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Resolved granular access for the current user against a single table:
/// scopes, the visible/editable field subsets, and the effective view filter.
/// </summary>
public class TableAccessContext
{
    /// <summary>True when no granular config applies (super admin, non-member, or table not configured).</summary>
    public bool Unrestricted { get; init; }
    public string ViewScope { get; init; } = RecordScopes.AllRecords;
    public string ModifyScope { get; init; } = RecordScopes.AllRecords;
    public bool CanAdd { get; init; } = true;
    public bool CanDelete { get; init; } = true;

    /// <summary>Fields the user may see (hidden fields removed). Use for record projection.</summary>
    public IReadOnlyList<AppField> VisibleFields { get; init; } = Array.Empty<AppField>();
    /// <summary>Field ids the user may write (excludes hidden + view-only + system fields).</summary>
    public IReadOnlySet<long> EditableFieldIds { get; init; } = new HashSet<long>();

    /// <summary>Role-defined record filter (null when none).</summary>
    public FilterGroup? ViewFilter { get; init; }
    /// <summary>When set, restrict rows to CreatedBy = this user id (OwnRecords scope).</summary>
    public long? RestrictToCreatedBy { get; init; }

    public bool CanView => Unrestricted || ViewScope != RecordScopes.None;
}

public interface IRolePermissionEnforcer
{
    Task<TableAccessContext> GetTableAccessAsync(AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct = default);

    /// <summary>Throw if OwnRecords scope applies and the record was not created by the current user.</summary>
    Task EnsureRecordOwnedAsync(AppTable table, Guid recordPublicId, CancellationToken ct = default);

    /// <summary>
    /// Privileged-write check for Action Button invocations (Field-Type spec Rule 1): a user
    /// with view-only / zero edit rights must still be able to use a button that writes data,
    /// without a field-edit-permission error — but ONLY to the fields the button is explicitly
    /// configured to set. This method enforces record-level visibility exactly like
    /// <see cref="GetTableAccessAsync"/> (a user who cannot see the record cannot act on it, and
    /// OwnRecords scope still requires ownership) but — unlike normal edits — does NOT require
    /// <c>ModifyScope != None</c> and does NOT check <c>EditableFieldIds</c>. Callers MUST ensure
    /// the write set passed to the record-write path is exactly <paramref name="buttonTargetFids"/>
    /// (the button's configured Capture/AddData/Timestamp/IP/Geo targets) — this is not a general
    /// edit grant.
    /// </summary>
    Task EnsureButtonWriteAllowedAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid recordPublicId,
        IReadOnlySet<long> buttonTargetFids, CancellationToken ct = default);
}
