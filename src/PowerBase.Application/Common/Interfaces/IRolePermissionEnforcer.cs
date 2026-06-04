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
}
