using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Fields.Queries.ListFields;

public class ListAllFieldsResult
{
    public IReadOnlyList<AppFieldListItemDto> Items { get; init; } = Array.Empty<AppFieldListItemDto>();
}

/// <summary>Genuinely unpaginated counterpart to <see cref="ListFieldsQueryHandler"/> — same
/// search/sort/filter validation and IsKeyField resolution, but no Page/PageSize/Total: the
/// result IS the complete field list, so its own Items.Count is both numbers at once.</summary>
public class ListAllFieldsQueryHandler
{
    /// <summary>Fid of the built-in Record ID# field — mirrors ListFieldsQueryHandler.RecordIdFid.</summary>
    private const int RecordIdFid = 3;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;

    public ListAllFieldsQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task<ListAllFieldsResult> HandleAsync(ListAllFieldsQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var sortBy = ListFieldsQueryHandler.AllowedSortFields.Contains(query.SortBy) ? query.SortBy : "name";

        var items = await _fieldRepo.ListByTableFilteredAsync(table.Id, query.Search, sortBy, query.SortDesc, query.Filter, ct);

        foreach (var item in items)
        {
            item.IsKeyField = table.KeyFieldId.HasValue
                ? item.Id == table.KeyFieldId.Value
                : item.Fid == RecordIdFid;
        }

        return new ListAllFieldsResult { Items = items };
    }
}
