using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Reports.Commands.UpdateDefaultReportSettings;

public class UpdateDefaultReportSettingsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IAppAccessService _appAccessService;

    public UpdateDefaultReportSettingsCommandHandler(
        IAppTableRepository tableRepo,
        IAppRoleRepository appRoleRepo,
        IReportRepository reportRepo,
        IAppAccessService appAccessService)
    {
        _tableRepo = tableRepo;
        _appRoleRepo = appRoleRepo;
        _reportRepo = reportRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(UpdateDefaultReportSettingsCommand command, CancellationToken ct = default)
    {
        await _appAccessService.RequirePermissionByTablePublicIdAsync(command.TablePublicId, PermissionCodes.TablesUpdate, ct);

        if (!DefaultReportModes.IsValid(command.Mode))
            throw new ValidationException(new Dictionary<string, string[]> { ["mode"] = ["Mode must be Everyone or RoleBased."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        if (!await _reportRepo.BelongsToTableAsync(command.TablePublicId, command.EveryoneReportId, ct))
            throw new ValidationException(new Dictionary<string, string[]> { ["everyoneReportId"] = ["Everyone report must belong to the selected table."] });

        var roleDefaults = new Dictionary<string, string>();
        foreach (var (rolePublicId, reportPublicId) in command.RoleDefaults)
        {
            var role = await _appRoleRepo.GetByPublicIdAsync(rolePublicId, ct)
                ?? throw new ValidationException(new Dictionary<string, string[]> { ["roleDefaults"] = [$"Role '{rolePublicId}' was not found."] });

            if (role.AppId != table.AppId)
                throw new ValidationException(new Dictionary<string, string[]> { ["roleDefaults"] = [$"Role '{rolePublicId}' does not belong to this table's app."] });

            if (reportPublicId is null)
                continue;

            if (!await _reportRepo.BelongsToTableAsync(command.TablePublicId, reportPublicId.Value, ct))
                throw new ValidationException(new Dictionary<string, string[]> { ["roleDefaults"] = [$"Report '{reportPublicId}' does not belong to the selected table."] });

            roleDefaults[rolePublicId.ToString()] = reportPublicId.Value.ToString();
        }

        await _reportRepo.SetDefaultAsync(command.TablePublicId, command.EveryoneReportId, ct);

        var settings = new DefaultReportSettingsDocument
        {
            Mode = command.Mode,
            RoleDefaults = command.Mode == DefaultReportModes.RoleBased ? roleDefaults : [],
        };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await _tableRepo.UpdateDefaultReportSettingsAsync(command.TablePublicId, json, ct);
    }
}

