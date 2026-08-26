using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Queries.GetField;

/// <summary>The field plus whether its table has any records — the latter drives whether the
/// field's encryption setting can still be changed (see FieldDetailResponse.HasRecords). An
/// EXISTS check, not a COUNT, so it stays cheap on tables with millions of records.</summary>
public sealed record GetFieldResult(AppField Field, bool HasRecords);

public class GetFieldQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;

    public GetFieldQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
    }

    public async Task<GetFieldResult> HandleAsync(GetFieldQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var field = await _fieldRepo.GetByPublicIdAsync(query.FieldPublicId, ct)
            ?? throw new NotFoundException("Field", query.FieldPublicId);

        // Defend against a caller supplying a field PublicId from a different table than the one
        // named in the route (and thus than the one the permission check above just authorized).
        if (field.AppTableId != table.Id)
            throw new NotFoundException("Field", query.FieldPublicId);

        var hasRecords = await _recordRepo.HasAnyRecordsAsync(table, ct);
        return new GetFieldResult(field, hasRecords);
    }
}
