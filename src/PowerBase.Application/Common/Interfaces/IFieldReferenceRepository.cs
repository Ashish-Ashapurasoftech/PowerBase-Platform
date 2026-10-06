using PowerBase.Application.FieldReferences;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IFieldReferenceRepository
{
    /// <summary>Atomically replaces every row of one source with <paramref name="rows"/> (empty = remove them all).</summary>
    Task ReplaceForSourceAsync(string sourceType, long sourceId, IReadOnlyCollection<FieldReferenceRow> rows, CancellationToken ct = default);

    /// <summary>Removes a source's rows by its public id — works after the source was soft-deleted.</summary>
    Task DeleteBySourcePublicIdAsync(string sourceType, Guid sourcePublicId, CancellationToken ct = default);

    /// <summary>Every live report of the table — including other users' personal ones and the hidden
    /// Default Report Settings row, which the user-facing list queries filter out. Only Id, PublicId,
    /// AppTableId, Name and Definition are populated.</summary>
    Task<IReadOnlyList<Report>> ListReportSourcesAsync(long tableId, CancellationToken ct = default);

    /// <summary>Removes the rows of every rule of the form, by the form's public id.</summary>
    Task DeleteRuleRowsOfFormAsync(Guid formPublicId, CancellationToken ct = default);

    /// <summary>Every reference to <paramref name="targetFieldId"/> whose source still exists (not soft-deleted).</summary>
    Task<IReadOnlyList<FieldReferenceItem>> ListByTargetFieldAsync(long targetFieldId, CancellationToken ct = default);

    Task<bool> IsTableIndexedAsync(long tableId, CancellationToken ct = default);
    Task MarkTableIndexedAsync(long tableId, CancellationToken ct = default);

    /// <summary>Removes every row whose source lives on the table, plus its indexed marker.</summary>
    Task ClearTableAsync(long tableId, CancellationToken ct = default);
}
