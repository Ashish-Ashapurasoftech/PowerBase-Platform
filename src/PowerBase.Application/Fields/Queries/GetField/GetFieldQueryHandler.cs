using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Queries.GetField;

public class GetFieldQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;

    public GetFieldQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task<AppField> HandleAsync(GetFieldQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var field = await _fieldRepo.GetByPublicIdAsync(query.FieldPublicId, ct)
            ?? throw new NotFoundException("Field", query.FieldPublicId);

        // Defend against a caller supplying a field PublicId from a different table than the one
        // named in the route (and thus than the one the permission check above just authorized).
        if (field.AppTableId != table.Id)
            throw new NotFoundException("Field", query.FieldPublicId);

        return field;
    }
}
