using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IReportRepository
{
    Task<Report> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<long> GetAppIdByPublicIdAsync(Guid reportPublicId, CancellationToken ct = default);
    Task<IReadOnlyList<Report>> ListByAppAsync(long appId, CancellationToken ct = default);
    Task<IReadOnlyList<Report>> ListByTableAsync(Guid tablePublicId, CancellationToken ct = default);
    Task<Report?> GetDefaultByTableAsync(Guid tablePublicId, CancellationToken ct = default);
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
}
