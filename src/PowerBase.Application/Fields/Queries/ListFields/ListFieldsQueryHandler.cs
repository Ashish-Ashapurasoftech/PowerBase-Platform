using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Fields.Queries.ListFields;

public class ListFieldsResult
{
    public IReadOnlyList<AppFieldListItemDto> Items { get; init; } = Array.Empty<AppFieldListItemDto>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ListFieldsQueryHandler
{
    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "name", "label", "description", "typeCode", "isRequired", "isSearchable", "isSortable",
        "isFilterable", "isReportable", "isAuditable", "isUnique", "isSystem", "fid", "createdOn",
    };

    /// <summary>Fid of the built-in Record ID# field — the implicit key when the table has no
    /// explicit KeyFieldId set. Mirrors SetKeyCommandHandler.RecordIdFid.</summary>
    private const int RecordIdFid = 3;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;

    public ListFieldsQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task<ListFieldsResult> HandleAsync(ListFieldsQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;
        var sortBy = AllowedSortFields.Contains(query.SortBy) ? query.SortBy : "name";

        var items = await _fieldRepo.ListByTablePagedAsync(table.Id, page, pageSize, query.Search, sortBy, query.SortDesc, query.Filter, ct);
        var total = await _fieldRepo.CountByTableAsync(table.Id, query.Search, query.Filter, ct);

        foreach (var item in items)
        {
            item.IsKeyField = table.KeyFieldId.HasValue
                ? item.Id == table.KeyFieldId.Value
                : item.Fid == RecordIdFid;
        }

        return new ListFieldsResult { Items = items, Total = total, Page = page, PageSize = pageSize };
    }
}
