using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Apps.Commands.CopyApp;

public class CopyAppResult
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsEncrypted { get; init; }
    public IReadOnlyList<AppCopyWarning> Warnings { get; init; } = [];
}

/// <summary>Copy App – Schema Only (plan: PLAN_Copy_App_Schema_Only.md). Checks who may copy and the name; the copy itself
/// is one SQL procedure (see <see cref="IAppCopyRepository"/>).</summary>
public class CopyAppCommandHandler
{
    private const string ADMINISTRATOR_ROLE = "Administrator";

    private readonly IAppRepository _appRepo;
    private readonly IAppCopyRepository _copyRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IUserRepository _userRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;

    public CopyAppCommandHandler(
        IAppRepository appRepo,
        IAppCopyRepository copyRepo,
        IAppUserRepository appUserRepo,
        IAppAccessService appAccessService,
        IUserRepository userRepo,
        IAuditRepository auditRepo,
        IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _copyRepo = copyRepo;
        _appUserRepo = appUserRepo;
        _appAccessService = appAccessService;
        _userRepo = userRepo;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
    }

    public async Task<CopyAppResult> HandleAsync(CopyAppCommand command, CancellationToken ct = default)
    {
        var validator = new CopyAppCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        // Members only (also enforces a user token's app list); then Administrators, or anyone when the app allows it.
        await _appAccessService.RequireMembershipByAppPublicIdAsync(command.AppPublicId, ct);
        var source = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        if (!_queryContext.IsSuperAdmin)
        {
            var roleName = await _appUserRepo.GetUserRoleNameAsync(source.Id, _queryContext.UserId, ct);
            if (roleName != ADMINISTRATOR_ROLE && !AllowsNonAdminsToCopy(source.SecurityOptions))
                throw new UnauthorizedActionException("Only app administrators can copy this app.");
        }

        var name = command.Name.Trim();
        var description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim();
        if (await _appRepo.NameExistsAsync(name, ct))
            throw new DuplicateException("App", "name", name);

        var copier = await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
        var copy = await _copyRepo.CopySchemaAsync(new AppSchemaCopyRequest(
            source.Id, name, description, _queryContext.UserId, copier.PublicId, copier.Name, copier.Email,
            command.CopyUsers, source.IsEncrypted, command.ProgressId), ct);

        // The copy is committed: log it even if the caller has gone away meanwhile (closed tab), so the request's token is not used.
        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.App, copy.NewAppPublicId.ToString(),
            $"Application copied from {source.Name}: {name}", appId: copy.NewAppId, ct: CancellationToken.None);
        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.App, copy.NewAppPublicId.ToString(),
            $"Application copied to {name}", appId: source.Id, ct: CancellationToken.None);

        return new CopyAppResult
        {
            PublicId = copy.NewAppPublicId,
            Name = name,
            Description = description,
            IsEncrypted = source.IsEncrypted,
            Warnings = copy.Warnings
        };
    }

    private static bool AllowsNonAdminsToCopy(string? securityOptions)
    {
        if (string.IsNullOrWhiteSpace(securityOptions) || !securityOptions.TrimStart().StartsWith('{'))
            return false;
        var settings = JsonSerializer.Deserialize<AppSecurityOptionsSettings>(
            securityOptions, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return settings?.AllowNonAdminsToCopy == true;
    }
}
