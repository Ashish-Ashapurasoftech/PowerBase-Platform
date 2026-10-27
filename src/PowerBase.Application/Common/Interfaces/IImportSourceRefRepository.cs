using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IImportSourceRefRepository
{
    Task<IReadOnlyList<ImportSourceRef>> ListByAppAsync(long appId, CancellationToken ct = default);

    /// <summary>Inserts the ref, or re-points an existing (app, type, logical ref) row at the new
    /// element — safe to call again on retry.</summary>
    Task UpsertManyAsync(long appId, IReadOnlyList<(string ElementType, string SourceLogicalRef, long ElementId)> refs, CancellationToken ct = default);

    Task UpsertAsync(long appId, string elementType, string sourceLogicalRef, long elementId, CancellationToken ct = default);
}
