using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Search;
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
    /// <summary>Live records copied; null for a copy without data.</summary>
    public long? RecordsCopied { get; init; }
}

/// <summary>Copy App – Schema Only (plan: PLAN_Copy_App_Schema_Only.md) and Schema + Data (PLAN_Copy_App_Schema_Data.md).
/// Checks who may copy and the name; the copy itself is done by SQL procedures (see <see cref="IAppCopyRepository"/>).</summary>
public class CopyAppCommandHandler
{
    private const string ADMINISTRATOR_ROLE = "Administrator";

    private readonly IAppRepository _appRepo;
    private readonly IAppCopyRepository _copyRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IUserRepository _userRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IMessagePublisher _messagePublisher;
    private readonly IQueryContext _queryContext;

    public CopyAppCommandHandler(
        IAppRepository appRepo,
        IAppCopyRepository copyRepo,
        IAppUserRepository appUserRepo,
        IAppAccessService appAccessService,
        IUserRepository userRepo,
        IAuditRepository auditRepo,
        IMessagePublisher messagePublisher,
        IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _copyRepo = copyRepo;
        _appUserRepo = appUserRepo;
        _appAccessService = appAccessService;
        _userRepo = userRepo;
        _auditRepo = auditRepo;
        _messagePublisher = messagePublisher;
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
        var isAdministrator = _queryContext.IsSuperAdmin
            || await _appUserRepo.GetUserRoleNameAsync(source.Id, _queryContext.UserId, ct) == ADMINISTRATOR_ROLE;
        if (!isAdministrator && !AllowsNonAdminsToCopy(source.SecurityOptions))
            throw new UnauthorizedActionException("Only app administrators can copy this app.");
        // A copy with data skips role record filters and field restrictions, so it would show a non-administrator data
        // the original hides from them.
        if (command.WithData && !isAdministrator)
            throw new UnauthorizedActionException("Only app administrators can copy this app with its data.");

        var name = command.Name.Trim();
        var description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim();
        if (await _appRepo.NameExistsAsync(name, ct))
            throw new DuplicateException("App", "name", name);

        var copier = await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
        var schemaRequest = new AppSchemaCopyRequest(
            source.Id, name, description, _queryContext.UserId, copier.PublicId, copier.Name, copier.Email,
            command.CopyUsers, source.IsEncrypted, command.ProgressId);

        long newAppId;
        Guid newAppPublicId;
        IReadOnlyList<AppCopyWarning> warnings;
        long? recordsCopied = null;
        if (command.WithData)
        {
            // There is no background job: copies a server restart cut off are removed when the next one starts.
            await DiscardStaleCopiesAsync();
            // Once the checks passed, the copy no longer depends on the request: a refreshed or closed tab (the dialog may
            // be closed while it runs) would otherwise roll back its first step, run row included, without a trace.
            var copy = await _copyRepo.CopyWithDataAsync(new AppDataCopyRequest(schemaRequest, command.IncludeFiles), CancellationToken.None);
            (newAppId, newAppPublicId, warnings, recordsCopied) = (copy.NewAppId, copy.NewAppPublicId, copy.Warnings, copy.RecordsCopied);
            await ReindexForSearchAsync(copy);
        }
        else
        {
            var copy = await _copyRepo.CopySchemaAsync(schemaRequest, ct);
            (newAppId, newAppPublicId, warnings) = (copy.NewAppId, copy.NewAppPublicId, copy.Warnings);
        }

        // The copy is committed: log it even if the caller has gone away meanwhile (closed tab), so the request's token is not used.
        var copiedFrom = recordsCopied is { } records
            ? $"Application copied from {source.Name} with {records} records: {name}"
            : $"Application copied from {source.Name}: {name}";
        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.App, newAppPublicId.ToString(),
            copiedFrom, appId: newAppId, ct: CancellationToken.None);
        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.App, newAppPublicId.ToString(),
            $"Application copied to {name}", appId: source.Id, ct: CancellationToken.None);

        return new CopyAppResult
        {
            PublicId = newAppPublicId,
            Name = name,
            Description = description,
            IsEncrypted = source.IsEncrypted,
            Warnings = warnings,
            RecordsCopied = recordsCopied
        };
    }

    private async Task DiscardStaleCopiesAsync()
    {
        try
        {
            await _copyRepo.DiscardStaleDataCopiesAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort: what is left behind is removed by a later copy; this copy does not depend on it.
        }
    }

    /// <summary>The copied records reach the search index the way a table moved to its own index does: one ReindexTable
    /// message per table, read page by page by the indexer in the background.</summary>
    private async Task ReindexForSearchAsync(AppDataCopyResult copy)
    {
        if (copy.TablesWithRecords.Count == 0) return;
        try
        {
            await _messagePublisher.PublishBatchAsync(
                copy.TablesWithRecords.Select(tableId => SearchWideMigration.Message(_queryContext.TenantId, copy.NewAppId, tableId, 1, afterId: 0)),
                CancellationToken.None);
        }
        catch (Exception)
        {
            // The copy is done; its records are found by searches once the table is reindexed (any later recovery reindex).
        }
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
