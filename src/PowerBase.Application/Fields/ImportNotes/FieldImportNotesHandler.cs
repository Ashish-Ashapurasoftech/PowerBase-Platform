using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.ImportNotes;

/// <summary>One import note on a field, as the field page shows it.</summary>
public sealed record FieldImportNoteDto(
    Guid PublicId, string Severity, string Code, string Message, string? RecommendedAction, string? SourceLogicalRef, string? SourceType,
    int? SourceFieldId, bool IsPlaceholder, string? OriginalDefinition, string? OriginalMetadata, DateTime CreatedOn, DateTime? ResolvedOn);

/// <summary>What the fields grid needs to mark a field: how many unresolved notes it has and how severe the worst one is.</summary>
public sealed record FieldImportNoteSummaryDto(
    Guid FieldPublicId, int ActiveCount, int WarningCount, int ErrorCount, bool HasPlaceholder, string Severity);

/// <summary>Reads and resolves the notes an import attached to fields. Import notes are about the import — they are never part of the
/// field's own description or help text, and resolving one changes nothing about the field itself.</summary>
public class FieldImportNotesHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppFieldImportNoteRepository _noteRepo;
    private readonly IQueryContext _queryContext;

    public FieldImportNotesHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IAppFieldImportNoteRepository noteRepo, IQueryContext queryContext)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _noteRepo = noteRepo;
        _queryContext = queryContext;
    }

    /// <summary>The fields of the table that have unresolved notes, worst first.</summary>
    public async Task<IReadOnlyList<FieldImportNoteSummaryDto>> SummarizeTableAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
        var summaries = await _noteRepo.SummarizeByTableAsync(table.Id, ct);
        if (summaries.Count == 0) return [];

        var fields = (await _fieldRepo.ListByTableAsync(table.Id, ct)).ToDictionary(f => f.Id);
        return summaries
            .Where(s => fields.ContainsKey(s.AppFieldId))
            .Select(s => new FieldImportNoteSummaryDto(
                fields[s.AppFieldId].PublicId, s.ActiveCount, s.WarningCount, s.ErrorCount, s.HasPlaceholder,
                s.ErrorCount > 0 ? ImportNoteSeverities.Error : s.WarningCount > 0 ? ImportNoteSeverities.Warning : ImportNoteSeverities.Information))
            .ToList();
    }

    public async Task<IReadOnlyList<FieldImportNoteDto>> ListForFieldAsync(Guid tablePublicId, Guid fieldPublicId, CancellationToken ct = default)
    {
        var field = await FindFieldAsync(tablePublicId, fieldPublicId, ct);
        return (await _noteRepo.ListByFieldAsync(field.Id, ct)).Select(Map).ToList();
    }

    /// <summary>Marks one note — or, with no note given, all of the field's unresolved notes — resolved. Returns how many were resolved.</summary>
    public async Task<int> ResolveAsync(Guid tablePublicId, Guid fieldPublicId, Guid? notePublicId, CancellationToken ct = default)
    {
        var field = await FindFieldAsync(tablePublicId, fieldPublicId, ct);
        if (notePublicId is null)
            return await _noteRepo.ResolveByFieldAsync(field.Id, _queryContext.UserId, ct);

        return await _noteRepo.ResolveAsync(field.Id, notePublicId.Value, _queryContext.UserId, ct)
            ? 1
            : throw new NotFoundException("Import note", notePublicId.Value);
    }

    private async Task<AppField> FindFieldAsync(Guid tablePublicId, Guid fieldPublicId, CancellationToken ct)
    {
        var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
        var field = await _fieldRepo.GetByPublicIdAsync(fieldPublicId, ct) ?? throw new NotFoundException("Field", fieldPublicId);
        // A field of another table than the one the route named (and the permission check authorized) is not found.
        if (field.AppTableId != table.Id) throw new NotFoundException("Field", fieldPublicId);
        return field;
    }

    private static FieldImportNoteDto Map(AppFieldImportNote n) => new(
        n.PublicId, n.Severity, n.Code, n.Message, n.RecommendedAction, n.SourceLogicalRef, n.SourceType, n.SourceFieldId,
        n.IsPlaceholder, n.OriginalDefinition, n.OriginalMetadata, n.CreatedOn, n.ResolvedOn);
}
