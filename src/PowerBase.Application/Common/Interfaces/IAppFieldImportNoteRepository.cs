using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>Field-level notes written by an import (see <see cref="AppFieldImportNote"/>).</summary>
public interface IAppFieldImportNoteRepository
{
    /// <summary>Stores the notes. Existing notes are never replaced or removed.</summary>
    Task AddManyAsync(IReadOnlyList<AppFieldImportNote> notes, CancellationToken ct = default);

    /// <summary>Every note of the field, unresolved first, then oldest first.</summary>
    Task<IReadOnlyList<AppFieldImportNote>> ListByFieldAsync(long appFieldId, CancellationToken ct = default);

    /// <summary>For each field of the table that has at least one unresolved note: how many, and how severe.</summary>
    Task<IReadOnlyList<AppFieldImportNoteSummary>> SummarizeByTableAsync(long appTableId, CancellationToken ct = default);

    /// <summary>Marks the field's unresolved notes resolved (the repair is done, or the warning is accepted). Returns how many.</summary>
    Task<int> ResolveByFieldAsync(long appFieldId, long userId, CancellationToken ct = default);

    /// <summary>Marks one note resolved. Returns false when it does not exist on that field or is already resolved.</summary>
    Task<bool> ResolveAsync(long appFieldId, Guid notePublicId, long userId, CancellationToken ct = default);
}
