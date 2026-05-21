using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Apps.Queries.ListAppVariables;

public class ListAppVariablesQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppVariableRepository _variableRepo;
    private readonly IAppAccessService _appAccessService;

    public ListAppVariablesQueryHandler(
        IAppRepository appRepo,
        IAppVariableRepository variableRepo,
        IAppAccessService appAccessService)
    {
        _appRepo = appRepo;
        _variableRepo = variableRepo;
        _appAccessService = appAccessService;
    }

    public async Task<IReadOnlyList<AppVariable>> HandleAsync(ListAppVariablesQuery query, CancellationToken ct = default)
    {
        await _appAccessService.RequireByAppPublicIdAsync(query.AppPublicId, AppAccess.Read, ct);

        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);

        return await _variableRepo.ListAsync(appId, ct);
    }
}
