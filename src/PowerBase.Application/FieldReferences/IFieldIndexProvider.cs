namespace PowerBase.Application.FieldReferences;

/// <summary>Hands out the <see cref="TableFieldIndex"/> of tables other than the source's own — the
/// parent/child/target table of a Lookup, Summary or Report Link. Implementations cache per call.</summary>
public interface IFieldIndexProvider
{
    Task<TableFieldIndex?> GetByIdAsync(long tableId, CancellationToken ct = default);
    Task<TableFieldIndex?> GetByPublicIdAsync(Guid tablePublicId, CancellationToken ct = default);
}
