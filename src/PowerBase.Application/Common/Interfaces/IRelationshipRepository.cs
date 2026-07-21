using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IRelationshipRepository
{
    Task<(long Id, Guid PublicId)> CreateAsync(Relationship rel, CancellationToken ct = default);

    /// <summary>Set the relationship's proxy (display) lookup field after the lookups are created.</summary>
    Task UpdateProxyFieldAsync(long id, long? proxyFieldId, CancellationToken ct = default);

    /// <summary>Repoint the relationship at a new Reference field (Set Key cascade rewire).</summary>
    Task UpdateReferenceFieldAsync(long id, long referenceFieldId, int referenceFid, CancellationToken ct = default);

    Task<Relationship?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<Relationship?> GetByIdAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<Relationship>> ListByAppAsync(long appId, CancellationToken ct = default);

    /// <summary>All non-deleted relationships where the table is the parent OR the child.</summary>
    Task<IReadOnlyList<Relationship>> ListByTableAsync(long tableId, CancellationToken ct = default);

    /// <summary>Relationships where the table is the parent (drives Summary projection and delete-restrict).</summary>
    Task<IReadOnlyList<Relationship>> ListByParentTableAsync(long parentTableId, CancellationToken ct = default);

    /// <summary>Relationships where the table is the child (drives Lookup projection and the reference picker).</summary>
    Task<IReadOnlyList<Relationship>> ListByChildTableAsync(long childTableId, CancellationToken ct = default);

    Task<int> SoftDeleteAsync(Guid publicId, CancellationToken ct = default);
}
