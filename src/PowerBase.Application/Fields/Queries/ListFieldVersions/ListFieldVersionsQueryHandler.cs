using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Queries.ListFieldVersions;

public class FieldVersionListResult
{
    public IReadOnlyList<FieldVersionListItem> Items { get; init; } = [];
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    /// <summary>The field's current (highest) version number — drives which row's Restore action
    /// the frontend disables ("do not allow restoring the currently active version").</summary>
    public int CurrentVersion { get; init; }
}

public class ListFieldVersionsQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldVersionRepository _versionRepo;

    public ListFieldVersionsQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IFieldVersionRepository versionRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _versionRepo = versionRepo;
    }

    public async Task<FieldVersionListResult> HandleAsync(ListFieldVersionsQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var field = await _fieldRepo.GetByPublicIdAsync(query.FieldPublicId, ct)
            ?? throw new NotFoundException("Field", query.FieldPublicId);
        if (field.AppTableId != table.Id)
            throw new NotFoundException("Field", query.FieldPublicId);

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var (items, total) = await _versionRepo.ListByFieldAsync(field.Id, query.Page, pageSize, ct);
        var currentVersion = await _versionRepo.GetCurrentVersionNumberAsync(field.Id, ct);

        return new FieldVersionListResult
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = pageSize,
            CurrentVersion = currentVersion,
        };
    }
}
