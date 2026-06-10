using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface ISchemaEngineService
{
    Task CreateTableAsync(AppTable table, CancellationToken ct = default);
    Task AddColumnAsync(AppTable table, AppField field, CancellationToken ct = default);
    Task SetUniqueAsync(AppTable table, AppField field, bool enable, CancellationToken ct = default);
}
