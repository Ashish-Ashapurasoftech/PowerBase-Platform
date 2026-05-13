namespace PowerBase.Application.Common.Interfaces;

public interface IRecordRepository
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
        long tableId, int page, int pageSize, CancellationToken ct = default);
    Task<int> CountAsync(long tableId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> GetByIdAsync(long tableId, Guid publicId, CancellationToken ct = default);
    Task<Guid> CreateAsync(long tableId, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default);
    Task UpdateAsync(long tableId, Guid publicId, IReadOnlyDictionary<string, object?> values, byte[] rowVersion, CancellationToken ct = default);
    Task DeleteAsync(long tableId, Guid publicId, CancellationToken ct = default);
}
