using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IReportRepository
{
    Task<Report> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<Report>> ListByAppAsync(Guid appPublicId, CancellationToken ct = default);
    Task<long> CreateAsync(Report report, CancellationToken ct = default);
}
