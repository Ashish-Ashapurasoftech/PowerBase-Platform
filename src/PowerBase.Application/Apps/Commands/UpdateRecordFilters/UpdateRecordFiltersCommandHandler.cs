using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateRecordFilters;

public class UpdateRecordFiltersCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAuditRepository _auditRepo;

    public UpdateRecordFiltersCommandHandler(
        IAppRoleRepository appRoleRepo,
        IAppRolePermissionRepository permRepo,
        IAppTableRepository tableRepo,
        IAuditRepository auditRepo)
    {
        _appRoleRepo = appRoleRepo;
        _permRepo = permRepo;
        _tableRepo = tableRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateRecordFiltersCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
                   ?? throw new NotFoundException("AppRole", command.RolePublicId);

        var rows = new List<AppRoleRecordFilter>();
        foreach (var f in command.Filters)
        {
            if (f.Conditions.Count == 0) continue; // empty filter ⇒ no restriction; don't store
            var table = await _tableRepo.GetByPublicIdAsync(f.TablePublicId, ct);
            rows.Add(new AppRoleRecordFilter
            {
                AppRoleId = role.Id,
                AppTableId = table.Id,
                Conjunction = f.Conjunction == "OR" ? "OR" : "AND",
                FilterJson = JsonSerializer.Serialize(f.Conditions),
            });
        }

        await _permRepo.SetRecordFiltersAsync(role.Id, rows, null, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(),
            $"Record filters updated for role: {role.Name}", appId: role.AppId, ct: ct);
    }
}
