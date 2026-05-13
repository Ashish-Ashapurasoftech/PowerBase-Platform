using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppFieldRepository
{
    Task<IReadOnlyList<AppField>> ListByTableAsync(Guid tablePublicId, CancellationToken ct = default);
    Task<bool> NameExistsInTableAsync(long tableId, string name, CancellationToken ct = default);
    Task<long> CreateAsync(AppField field, CancellationToken ct = default);
}
