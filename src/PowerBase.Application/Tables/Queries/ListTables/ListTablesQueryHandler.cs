using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Tables.Queries.ListTables;

public class ListTablesQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;

    public ListTablesQueryHandler(IAppRepository appRepo, IAppTableRepository tableRepo)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
    }

    public async Task<IReadOnlyList<AppTable>> HandleAsync(ListTablesQuery query, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(query.AppPublicId, ct);
        return await _tableRepo.ListByAppAsync(app.Id, ct);
    }
}
