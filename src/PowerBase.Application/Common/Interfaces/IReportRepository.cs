using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IReportRepository
{
    Task<Report> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<Report>> ListByAppAsync(long appId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(Report report, CancellationToken ct = default);
}
