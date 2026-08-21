using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Fields.Queries.ListFieldTypes;

public class ListFieldTypesQueryHandler
{
    private readonly IFieldTypeRepository _fieldTypeRepo;

    public ListFieldTypesQueryHandler(IFieldTypeRepository fieldTypeRepo) => _fieldTypeRepo = fieldTypeRepo;

    public async Task<IReadOnlyList<FieldType>> HandleAsync(ListFieldTypesQuery query, CancellationToken ct = default)
        => await _fieldTypeRepo.ListAllAsync(ct);
}
