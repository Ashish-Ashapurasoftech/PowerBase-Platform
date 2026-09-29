using PowerBase.Application.Common.Models;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IReportRepository
{
    Task<Report> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);

    /// <summary>The report's owning table's internal id, or null if the report doesn't exist.
    /// Used to validate a client-supplied reportId (e.g. Grid Edit's per-report rule scoping in
    /// FormRuleServerValidator) actually belongs to the table being written, without throwing on
    /// a garbage/mismatched id — the caller falls back to the full table-wide rule check instead.</summary>
    Task<long?> GetAppTableIdByPublicIdAsync(Guid reportPublicId, CancellationToken ct = default);

    /// <summary>The report's currently ACTIVE, APPLIED Grid Edit rules — same resolution as
    /// GetGridEditRuntimeAsync (selected forms' applicable active rules, minus any explicitly
    /// excluded, in the report's own priority order), but as full FormRule domain entities
    /// (Conditions/Actions populated) for FormRuleServerValidator to enforce server-side. Only
    /// call once the report is confirmed to belong to the table being written.</summary>
    Task<IReadOnlyList<PowerBase.Domain.Entities.FormRule>> GetAppliedGridEditRulesAsync(Guid reportPublicId, CancellationToken ct = default);
    Task<long> GetAppIdByPublicIdAsync(Guid reportPublicId, CancellationToken ct = default);
    Task<IReadOnlyList<Report>> ListByAppAsync(long appId, CancellationToken ct = default);
    Task<IReadOnlyList<Report>> ListAllByAppAsync(long appId, CancellationToken ct = default);
    Task<IReadOnlyList<Report>> ListByTableAsync(Guid tablePublicId, CancellationToken ct = default);
    /// <summary>Slim, paged, searchable (by Name), sortable listing for the reports grid — same
    /// role-based Visibility filtering as <see cref="ListByTableAsync"/>, just paginated.</summary>
    Task<IReadOnlyList<ReportListItemDto>> ListByTablePagedAsync(
        Guid tablePublicId, int page, int pageSize, string? search, string sortBy, bool sortDesc, CancellationToken ct = default);
    Task<int> CountByTableAsync(Guid tablePublicId, string? search, CancellationToken ct = default);
    Task<Report?> GetDefaultByTableAsync(Guid tablePublicId, CancellationToken ct = default);
    /// <summary>The hidden, per-table row backing "Default Report Settings" — see
    /// <see cref="Report.IsDefaultSettingsRecord"/>. Null only if migration
    /// 055_add_report_default_settings_record.sql hasn't run yet for this tenant; callers should
    /// lazily create one (see GetOrCreateDefaultReportSettingsQueryHandler) rather than assume
    /// every table already has one.</summary>
    Task<Report?> GetDefaultSettingsRecordAsync(Guid tablePublicId, CancellationToken ct = default);
    Task<Report?> GetVisibleReportAsync(Guid publicId, CancellationToken ct = default);
    Task<Report?> GetFirstVisibleReportByTableAsync(Guid tablePublicId, CancellationToken ct = default);
    Task<bool> BelongsToTableAsync(Guid tablePublicId, Guid reportPublicId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(Report report, CancellationToken ct = default);
    Task<int> UpdateAsync(Guid publicId, string name, string? description,
        string visibility, string definition, CancellationToken ct = default);
    Task SetDefaultAsync(Guid tablePublicId, Guid reportPublicId, CancellationToken ct = default);
    Task UpdateFormOverridesAsync(Guid tablePublicId, IEnumerable<(Guid ReportPublicId, Guid? ViewEditFormPublicId)> overrides, CancellationToken ct = default);
    Task<int> DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task SetReportRolesAsync(long reportId, IEnumerable<long> roleIds, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetReportRoleIdsAsync(long reportId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetReportRolePublicIdsAsync(long reportId, CancellationToken ct = default);
    Task<Dictionary<long, List<long>>> GetAppRoleReportsMapAsync(long appId, CancellationToken ct = default);

    // ── Grid Edit & Form Rules — a report selects FORMS; every active applicable rule of a selected
    //    form applies automatically except the ones the user excluded. Client-side pre-check config
    //    only: server-side write enforcement (FormRuleServerValidator) is unaffected by it. ──

    /// <summary>Every form on the table with its applicable-rule count (one aggregate query).</summary>
    Task<IReadOnlyList<GridEditFormOption>> ListGridEditFormOptionsAsync(long appTableId, CancellationToken ct = default);
    /// <summary>The forms this report has selected, in the order saved.</summary>
    Task<IReadOnlyList<Guid>> GetGridEditFormIdsAsync(Guid reportPublicId, CancellationToken ct = default);
    /// <summary>Applicable, active rules of the report's SELECTED forms only, in the report's priority
    /// order (stored order first, then rules with no stored row — e.g. added since — in form/rule order).</summary>
    Task<IReadOnlyList<GridEditRuleState>> ListGridEditRuleStatesAsync(Guid reportPublicId, CancellationToken ct = default);
    /// <summary>Applicable, active rules of ONE form — fetched when the user selects that form.</summary>
    Task<IReadOnlyList<GridEditRuleItem>> ListGridEditRulesForFormAsync(long appTableId, Guid formPublicId, CancellationToken ct = default);
    /// <summary>Replaces the report's whole Grid Edit config in one transaction. <paramref name="appliedRuleIds"/>
    /// is the priority order; <paramref name="excludedRuleIds"/> are rules of selected forms moved aside.</summary>
    Task SetGridEditConfigAsync(Guid reportPublicId, IReadOnlyList<Guid> formIds,
        IReadOnlyList<Guid> appliedRuleIds, IReadOnlyList<Guid> excludedRuleIds, CancellationToken ct = default);
    /// <summary>What the grid needs to enforce the applied rules, in one round trip.</summary>
    Task<IReadOnlyList<GridEditRuntimeRule>> GetGridEditRuntimeAsync(Guid reportPublicId, CancellationToken ct = default);
}
