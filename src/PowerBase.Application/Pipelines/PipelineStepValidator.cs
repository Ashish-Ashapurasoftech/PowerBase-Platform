using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Repositories resolved from a target-tenant scope.
/// The handler disposes the scope after validation completes.
/// </summary>
public sealed class TargetTenantRepos : IAsyncDisposable
{
    public IAppRepository AppRepo { get; init; } = null!;
    public IAppTableRepository TableRepo { get; init; } = null!;
    public IAppFieldRepository FieldRepo { get; init; } = null!;
    public IAppAccessService AppAccessService { get; init; } = null!;

    private readonly IAsyncDisposable? _scope;

    public TargetTenantRepos(
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppAccessService appAccessService,
        IAsyncDisposable? scope = null)
    {
        AppRepo = appRepo;
        TableRepo = tableRepo;
        FieldRepo = fieldRepo;
        AppAccessService = appAccessService;
        _scope = scope;
    }

    public async ValueTask DisposeAsync()
    {
        if (_scope is not null)
            await _scope.DisposeAsync();
    }
}

public class PipelineStepValidator
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly ITenantRepository _tenantRepo;
    private readonly IQueryContext _queryContext;

    /// <summary>
    /// Optional factory that creates a scoped repository set for a given target TenantId.
    /// When null, same-tenant owner repositories are used (no cross-tenant support).
    /// The factory MUST:
    ///   1. Create a fresh IServiceScope.
    ///   2. Resolve IQueryContext from that scope and call SetTenantId(targetTenantId).
    ///   3. Resolve IAppRepository, IAppTableRepository, IAppFieldRepository, IAppAccessService
    ///      from that same scope.
    ///   4. Return a TargetTenantRepos instance wrapping the scope for disposal.
    /// </summary>
    private readonly Func<long, Task<TargetTenantRepos>>? _targetScopeFactory;

    public static readonly HashSet<Guid> SystemConnectionIds = new()
    {
        new Guid("00000000-0000-0000-0000-000000000001"),
        new Guid("00000000-0000-0000-0000-000000000002"),
        new Guid("00000000-0000-0000-0000-000000000003")
    };

    public PipelineStepValidator(
        IPipelineRepository pipelineRepo,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppAccessService appAccessService,
        ITenantRepository tenantRepo,
        IQueryContext queryContext,
        Func<long, Task<TargetTenantRepos>>? targetScopeFactory = null)
    {
        _pipelineRepo = pipelineRepo;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _appAccessService = appAccessService;
        _tenantRepo = tenantRepo;
        _queryContext = queryContext;
        _targetScopeFactory = targetScopeFactory;
    }

    public async Task ValidateStepConnectionAndTenantAccessAsync(string configJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;

        using var doc = JsonDocument.Parse(configJson);
        var root = doc.RootElement;
        
        string? connectionPublicId = null;
        if (root.TryGetProperty("connectionPublicId", out var prop1) && prop1.ValueKind == JsonValueKind.String)
        {
            connectionPublicId = prop1.GetString();
        }
        else if (root.TryGetProperty("ConnectionPublicId", out var prop2) && prop2.ValueKind == JsonValueKind.String)
        {
            connectionPublicId = prop2.GetString();
        }

        if (string.IsNullOrEmpty(connectionPublicId)) return;

        if (!Guid.TryParse(connectionPublicId, out var connectionGuid))
        {
            throw new ValidationException(new Dictionary<string, string[]> { { "ConnectionPublicId", new[] { "Connection must be a valid Guid." } } });
        }

        if (SystemConnectionIds.Contains(connectionGuid))
        {
            return;
        }

        try
        {
            var tenant = await _tenantRepo.GetTenantForUserAsync(connectionGuid, _queryContext.UserId, ct);
            if (tenant != null) return;
        }
        catch
        {
            // fallback to check pipeline connection
        }

        var connection = await _pipelineRepo.GetConnectionByPublicIdAsync(connectionGuid, ct);
        if (connection == null || connection.IsDeleted)
        {
            throw new ValidationException(new Dictionary<string, string[]> { { "ConnectionPublicId", new[] { "Connection does not exist or is inactive." } } });
        }
    }

    public async Task ValidateNewEventStepAsync(string configJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw new ValidationException(new Dictionary<string, string[]> { { "ConfigJson", new[] { "Configuration is required." } } });

        NewEventStepConfig config;
        try
        {
            config = JsonSerializer.Deserialize<NewEventStepConfig>(configJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException();
        }
        catch
        {
            throw new ValidationException(new Dictionary<string, string[]> { { "ConfigJson", new[] { "Configuration is malformed." } } });
        }

        var errors = new Dictionary<string, List<string>>();

        // 1. Connection — resolve target tenant for cross-tenant step metadata validation
        long targetTenantId = _queryContext.TenantId; // default: same as owner
        if (string.IsNullOrEmpty(config.ConnectionPublicId) || !Guid.TryParse(config.ConnectionPublicId, out var connectionGuid))
        {
            AddError(errors, "ConnectionPublicId", "Connection is required and must be a valid Guid.");
        }
        else if (!SystemConnectionIds.Contains(connectionGuid))
        {
            Guid resolvedConnectionGuid = connectionGuid;
            try
            {
                // connectionPublicId == tenantPublicId for tenant connections
                var tenant = await _tenantRepo.GetTenantForUserAsync(resolvedConnectionGuid, _queryContext.UserId, ct);
                if (tenant == null) throw new InvalidOperationException();
                targetTenantId = tenant.Id; // resolved target tenant's internal Id
            }
            catch
            {
                var connection = await _pipelineRepo.GetConnectionByPublicIdAsync(connectionGuid, ct);
                if (connection == null || connection.IsDeleted)
                {
                    AddError(errors, "ConnectionPublicId", "Connection does not exist or is inactive.");
                }
            }
        }

        // If we already have connection errors, bail out early — downstream validations are meaningless
        if (errors.Any())
        {
            throw new ValidationException(errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()));
        }

        // 2-4. App / Table / Field validation — use target-tenant scope for cross-tenant connections
        bool isTargetTenant = targetTenantId != _queryContext.TenantId;
        if (isTargetTenant && _targetScopeFactory is not null)
        {
            // Create a fresh scope scoped to the target tenant's database
            await using var targetRepos = await _targetScopeFactory(targetTenantId);
            await ValidateAppTableFieldsAsync(config, targetRepos.AppRepo, targetRepos.TableRepo, targetRepos.FieldRepo, targetRepos.AppAccessService, errors, ct);
        }
        else
        {
            // Same-tenant: use the owner-request's injected repositories (original behavior)
            await ValidateAppTableFieldsAsync(config, _appRepo, _tableRepo, _fieldRepo, _appAccessService, errors, ct);
        }

        if (errors.Any())
        {
            throw new ValidationException(errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()));
        }
    }

    // ---------------------------------------------------------------------------
    // Core App/Table/Field validation — scoped to the given repositories
    // ---------------------------------------------------------------------------

    private static async Task ValidateAppTableFieldsAsync(
        NewEventStepConfig config,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppAccessService appAccessService,
        Dictionary<string, List<string>> errors,
        CancellationToken ct)
    {
        // 2. App
        long appId = 0;
        if (string.IsNullOrEmpty(config.AppPublicId) || !Guid.TryParse(config.AppPublicId, out var appGuid))
        {
            AddError(errors, "AppPublicId", "App is required and must be a valid Guid.");
        }
        else
        {
            try
            {
                var app = await appRepo.GetByPublicIdAsync(appGuid, ct);
                appId = app.Id;
                await appAccessService.RequirePermissionByAppPublicIdAsync(appGuid, PermissionCodes.PowerFlowsUpdate, ct);
            }
            catch (Exception ex)
            {
                AddError(errors, "AppPublicId", $"App is inaccessible or invalid: {ex.Message}");
            }
        }

        // 3. Table
        long tableId = 0;
        if (string.IsNullOrEmpty(config.TablePublicId) || !Guid.TryParse(config.TablePublicId, out var tableGuid))
        {
            AddError(errors, "TablePublicId", "Table is required and must be a valid Guid.");
        }
        else
        {
            try
            {
                var table = await tableRepo.GetByPublicIdAsync(tableGuid, ct);
                tableId = table.Id;
                if (appId > 0 && table.AppId != appId)
                {
                    AddError(errors, "TablePublicId", "Selected table does not belong to the selected App.");
                }
            }
            catch
            {
                AddError(errors, "TablePublicId", "Table is inaccessible or invalid.");
            }
        }

        // 4. Fields
        if (tableId > 0)
        {
            var fields = await fieldRepo.ListByTableAsync(tableId, ct);

            if (config.TriggerFields != null && config.TriggerFields.Any() && !config.TriggerOnAnyField)
            {
                foreach (var fidStr in config.TriggerFields)
                {
                    var fid = ParseFid(fidStr);
                    if (fid == null || !fields.Any(f => f.Fid == fid.Value))
                    {
                        AddError(errors, "TriggerFields", $"Field '{fidStr}' does not exist in the selected Table.");
                    }
                }
            }

            if (config.SubsequentFields != null && config.SubsequentFields.Any())
            {
                foreach (var fidStr in config.SubsequentFields)
                {
                    var fid = ParseFid(fidStr);
                    if (fid == null || !fields.Any(f => f.Fid == fid.Value))
                    {
                        AddError(errors, "SubsequentFields", $"Field '{fidStr}' does not exist in the selected Table.");
                    }
                }
            }

            // 4b. Filters Validation
            if (config.Filters != null)
            {
                var mockGroup = new TriggerFilterGroup { LogicalOp = "AND", Rules = config.Filters };
                if (!PipelineFilterEvaluator.IsGroupCompletelyBlank(mockGroup))
                {
                    PipelineFilterEvaluator.ValidateGroup(mockGroup, fields, errors, "Filters");
                }
            }
            if (config.FilterGroups != null)
            {
                for (int i = 0; i < config.FilterGroups.Count; i++)
                {
                    if (!PipelineFilterEvaluator.IsGroupCompletelyBlank(config.FilterGroups[i]))
                    {
                        PipelineFilterEvaluator.ValidateGroup(config.FilterGroups[i], fields, errors, $"FilterGroups[{i}]");
                    }
                }
            }
        }

        // 5. Options
        if (!config.TriggerOnAdded && !config.TriggerOnModified && !config.TriggerOnDeleted)
        {
            AddError(errors, "TriggerOptions", "At least one event (Added, Modified, or Deleted) must be selected.");
        }

        // 6. Max Records
        if (config.LimitRecords)
        {
            if (!config.MaxRecords.HasValue || config.MaxRecords.Value <= 0)
            {
                AddError(errors, "MaxRecords", "Maximum number of records must be a positive integer.");
            }
        }
    }

    private static void AddError(Dictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var list))
        {
            list = new List<string>();
            errors[key] = list;
        }
        list.Add(message);
    }

    private static int? ParseFid(string fidStr)
    {
        if (string.IsNullOrEmpty(fidStr)) return null;
        var s = fidStr.ToLower().Trim();
        if (s.StartsWith("fid_") && int.TryParse(s.Substring(4), out var result))
        {
            return result;
        }
        if (int.TryParse(s, out var directResult))
        {
            return directResult;
        }
        return null;
    }

    private class NewEventStepConfig
    {
        public string? ConnectionPublicId { get; set; }
        public string? AppPublicId { get; set; }
        public string? TablePublicId { get; set; }
        public bool TriggerOnAdded { get; set; }
        public bool TriggerOnModified { get; set; }
        public bool TriggerOnDeleted { get; set; }
        public bool TriggerOnAnyField { get; set; }
        public List<string>? TriggerFields { get; set; }
        public List<string>? SubsequentFields { get; set; }
        public bool LimitRecords { get; set; }
        public int? MaxRecords { get; set; }
        public List<TriggerFilterRule>? Filters { get; set; }
        public List<TriggerFilterGroup>? FilterGroups { get; set; }
    }
}
