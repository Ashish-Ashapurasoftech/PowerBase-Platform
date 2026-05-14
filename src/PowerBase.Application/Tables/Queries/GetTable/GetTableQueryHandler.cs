using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Tables.Queries.GetTable;

public record GetTableResult(AppTable Table, IReadOnlyList<AppField> Fields);

public class GetTableQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;

    public GetTableQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task<GetTableResult> HandleAsync(GetTableQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.PublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        return new GetTableResult(table, fields);
    }
}
