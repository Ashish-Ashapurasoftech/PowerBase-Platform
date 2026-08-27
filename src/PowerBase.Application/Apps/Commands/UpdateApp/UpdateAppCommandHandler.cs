using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateApp;

public class UpdateAppCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAuditRepository _auditRepo;

    public UpdateAppCommandHandler(IAppRepository appRepo, IAppAccessService appAccessService, IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _appAccessService = appAccessService;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateAppCommand command, CancellationToken ct = default)
    {
        // Enforce App Administrator privileges explicitly
        await _appAccessService.RequireAppRoleAsync(command.AppPublicId, "Administrator", ct);

        var validator = new UpdateAppCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var existingApp = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        if (existingApp == null)
            throw new NotFoundException("App", command.AppPublicId);

        PowerBase.Domain.ValueObjects.AppSecurityOptionsSettings? finalSecurityOptions = command.SecurityOptions;
        if (finalSecurityOptions == null && !string.IsNullOrEmpty(existingApp.SecurityOptions))
        {
            finalSecurityOptions = JsonSerializer.Deserialize<PowerBase.Domain.ValueObjects.AppSecurityOptionsSettings>(
                existingApp.SecurityOptions, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        else if (finalSecurityOptions != null && !string.IsNullOrEmpty(existingApp.SecurityOptions))
        {
            var existingSecurity = JsonSerializer.Deserialize<PowerBase.Domain.ValueObjects.AppSecurityOptionsSettings>(
                existingApp.SecurityOptions, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (existingSecurity != null && !string.IsNullOrEmpty(existingSecurity.WrappedDek))
            {
                finalSecurityOptions.WrappedDek = existingSecurity.WrappedDek;
            }
        }

        var formattingStr = command.Formatting != null ? JsonSerializer.Serialize(command.Formatting) : null;
        var securityStr = finalSecurityOptions != null ? JsonSerializer.Serialize(finalSecurityOptions) : null;
        
        var affected = await _appRepo.UpdateAsync(command.AppPublicId, command.Name, command.Description, command.Icon, command.Color, formattingStr, securityStr, command.IsEncrypted, ct);
        if (affected == 0)
            throw new NotFoundException("App", command.AppPublicId);

        await _auditRepo.LogActivityAsync(AuditActions.Updated, AuditEntityTypes.App, command.AppPublicId.ToString(), $"Application name changed to {command.Name}", ct: ct);
    }
}
