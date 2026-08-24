using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Queries.GetPipelineEditor;

/// <summary>
/// Loads a pipeline and its authoritative editor metadata in one operation.
///
/// Resolution strategy per connection type:
///  - No connection / empty: current-tenant repositories (direct injection)
///  - connectionPublicId == tenantPublicId (CurrentUser cross-tenant):
///    IServiceScopeFactory + IQueryContext.SetTenantId — same pattern as PipelineEngine
///  - connectionPublicId == PipelineAccount.PublicId (saved "Connect new account"):
///    resolved server-side inside the account's realm, as the account's token owner
///  - connectionPublicId == PipelineConnection.PublicId (SavedConnection):
///    Cannot resolve via PowerBase DB — returned as ClientResolveRef(SavedConnection)
///  - connectionPublicId in SystemConnectionIds:
///    External Quickbase API — returned as ClientResolveRef(SystemConnection)
/// </summary>
public class GetPipelineEditorQueryHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAdminRepository _adminRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IQueryContext _queryContext;
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Optional so the handler can still be constructed without saved-account support.
    /// When null, a saved account degrades to the "unknown GUID" branch instead of being
    /// resolved against the owner tenant.
    /// </summary>
    private readonly Connections.Common.ConnectionScopeResolver? _connectionScopeResolver;

    public GetPipelineEditorQueryHandler(
        IPipelineRepository pipelineRepo,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAdminRepository adminRepo,
        ITenantRepository tenantRepo,
        IQueryContext queryContext,
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory,
        Connections.Common.ConnectionScopeResolver? connectionScopeResolver = null)
    {
        _pipelineRepo = pipelineRepo;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _adminRepo = adminRepo;
        _tenantRepo = tenantRepo;
        _queryContext = queryContext;
        _scopeFactory = scopeFactory;
        _connectionScopeResolver = connectionScopeResolver;
    }

    public async Task<PipelineEditorResult> HandleAsync(GetPipelineEditorQuery query, CancellationToken ct = default)
    {
        // ── 1. Load pipeline ─────────────────────────────────────────────────────
        var pipeline = await _pipelineRepo.GetByPublicIdAsync(query.PublicId, ct);
        var appPublicId = await _appRepo.GetPublicIdByIdAsync(pipeline.AppId, ct);

        var flatSteps = await _pipelineRepo.GetStepsByPipelineIdAsync(pipeline.Id, ct);

        // ── 2. Reconstruct step hierarchy (identical to GetPipelineQueryHandler) ─
        var stepDtos = flatSteps.ToDictionary(
            s => s.Id,
            s => new PipelineEditorStepResult
            {
                PublicId = s.PublicId,
                RefId = s.RefId,
                Label = s.Label,
                Notes = s.Notes,
                IsValidated = s.IsValidated,
                LastTriggeredOn = s.LastTriggeredOn,
                DisplayOrder = s.DisplayOrder,
                Type = s.Type,
                Subtype = s.Subtype,
                ConfigJson = s.ConfigJson,
                ParentBranch = s.ParentBranch,
                RowVersion = s.RowVersion ?? Array.Empty<byte>()
            });

        var rootSteps = new List<PipelineEditorStepResult>();
        foreach (var step in flatSteps)
        {
            var dto = stepDtos[step.Id];
            if (step.ParentStepId.HasValue && stepDtos.TryGetValue(step.ParentStepId.Value, out var parentDto))
            {
                var branch = step.ParentBranch?.ToLowerInvariant();
                if (branch == "elsechildren") parentDto.ElseChildren.Add(dto);
                else if (branch == "successchildren") parentDto.SuccessChildren.Add(dto);
                else if (branch == "errorchildren") parentDto.ErrorChildren.Add(dto);
                else parentDto.Children.Add(dto);
            }
            else
            {
                rootSteps.Add(dto);
            }
        }
        SortSteps(rootSteps);

        // ── 3. Extract all table references from step configs ────────────────────
        var tableRefs = ExtractAllTableRefs(flatSteps);

        // ── 4. Resolve metadata per unique (connectionKey, tablePublicId) ─────────
        var editorTables = new List<PipelineEditorTableMetadata>();
        var clientRefs = new List<PipelineEditorClientRef>();

        // Deduplicate: key = "connectionPublicId::tablePublicId"
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tableRef in tableRefs)
        {
            var dedupKey = $"{tableRef.ConnectionPublicId ?? string.Empty}::{tableRef.TablePublicId}";
            if (!seen.Add(dedupKey)) continue;

            await ResolveTableRefAsync(tableRef, editorTables, clientRefs, ct);
        }

        return new PipelineEditorResult
        {
            PublicId = pipeline.PublicId,
            AppPublicId = appPublicId,
            Name = pipeline.Name,
            Description = pipeline.Description,
            VariablesJson = pipeline.VariablesJson,
            IsActive = pipeline.IsActive,
            RowVersion = pipeline.RowVersion ?? Array.Empty<byte>(),
            Steps = rootSteps,
            EditorTables = editorTables,
            ClientResolveRefs = clientRefs
        };
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Reference extraction
    // ────────────────────────────────────────────────────────────────────────────

    private record TableRefInfo(
        string? ConnectionPublicId, // null = current tenant / no connection
        Guid? AppPublicId,
        Guid TablePublicId);

    /// <summary>
    /// Walks all steps (recursively through all branch types) and extracts every
    /// unique table reference needed by the editor. Uses step Subtype to determine
    /// which config properties hold table public IDs — never scans arbitrary JSON.
    /// </summary>
    private static List<TableRefInfo> ExtractAllTableRefs(IEnumerable<PipelineStep> flatSteps)
    {
        var refs = new List<TableRefInfo>();
        foreach (var step in flatSteps)
        {
            ExtractFromStep(step, refs);
        }
        // Note: the repo returns a flat list; hierarchy reconstruction is done separately.
        // We iterate the full flat list here to ensure nested steps are not missed.
        return refs;
    }

    private static void ExtractFromStep(PipelineStep step, List<TableRefInfo> refs)
    {
        if (string.IsNullOrWhiteSpace(step.ConfigJson)) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(step.ConfigJson); }
        catch { return; } // malformed config — skip, do not crash editor load

        using (doc)
        {
            var root = doc.RootElement;
            var subtype = step.Subtype?.ToLowerInvariant() ?? string.Empty;

            // Extract connectionPublicId (null = current-tenant)
            string? connStr = TryGetString(root, "connectionPublicId");

            // Extract appPublicId
            Guid? appGuid = null;
            var appStr = TryGetString(root, "appPublicId") ?? TryGetString(root, "app");
            if (Guid.TryParse(appStr, out var ag)) appGuid = ag;

            // Single-table steps
            if (subtype is "search-records" or "create-record" or "bulk-trigger"
                or "new-event" or "new-bulk-event" or "prepare-bulk-upsert"
                or "import-with-csv" or "import-to-quickbase" or "export-records-csv"
                or "update-record" or "delete-record" or "upload-file"
                or "on-new-event" or "on-new-bulk-event")
            {
                var tableStr = TryGetString(root, "tablePublicId") ?? TryGetString(root, "tableId");
                if (Guid.TryParse(tableStr, out var tg))
                    refs.Add(new TableRefInfo(connStr, appGuid, tg));
            }

            // copy-records has two tables (sourceTable and destinationTable)
            if (subtype == "copy-records")
            {
                var srcStr = TryGetString(root, "sourceTable");
                var dstStr = TryGetString(root, "destinationTable");
                // These may be stored as "appId:tableId" format or raw GUID
                if (Guid.TryParse(ExtractTableGuidFromValue(srcStr), out var sg))
                    refs.Add(new TableRefInfo(connStr, appGuid, sg));
                if (Guid.TryParse(ExtractTableGuidFromValue(dstStr), out var dg))
                    refs.Add(new TableRefInfo(connStr, appGuid, dg));
            }
        }
    }

    /// <summary>
    /// Handles values stored as "appId:tableId" (colon-separated) or plain GUIDs.
    /// Returns the tablePublicId portion.
    /// </summary>
    private static string? ExtractTableGuidFromValue(string? val)
    {
        if (string.IsNullOrEmpty(val)) return null;
        if (val.Contains(':'))
        {
            var parts = val.Split(':', 2);
            return parts.Length > 1 ? parts[1] : val;
        }
        return val;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        // Also check PascalCase variant (some older configs may use it)
        var pascal = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (root.TryGetProperty(pascal, out var prop2) && prop2.ValueKind == JsonValueKind.String)
            return prop2.GetString();
        return null;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Metadata resolution per reference
    // ────────────────────────────────────────────────────────────────────────────

    private async Task ResolveTableRefAsync(
        TableRefInfo tableRef,
        List<PipelineEditorTableMetadata> editorTables,
        List<PipelineEditorClientRef> clientRefs,
        CancellationToken ct)
    {
        var connStr = tableRef.ConnectionPublicId;

        // ── System connections: frontend resolves via stored credentials ──────────
        if (!string.IsNullOrEmpty(connStr) && Guid.TryParse(connStr, out var connGuid))
        {
            if (PipelineStepValidator.SystemConnectionIds.Contains(connGuid))
            {
                clientRefs.Add(new PipelineEditorClientRef
                {
                    ConnectionPublicId = connStr,
                    AppPublicId = tableRef.AppPublicId,
                    TablePublicId = tableRef.TablePublicId,
                    Reason = PipelineEditorRefReason.SystemConnection
                });
                return;
            }

            // ── Non-system connection GUID: determine if tenant or SavedConnection ─
            // Step 1: Try resolving as a tenant public ID (CurrentUser cross-tenant)
            var resolvedTenantId = await _adminRepo.GetTenantIdByPublicIdAsync(connGuid, ct);
            if (resolvedTenantId.HasValue)
            {
                // CurrentUser cross-tenant: resolve via scoped tenant context
                await ResolveCrossTenantTableAsync(connStr, resolvedTenantId.Value, tableRef, editorTables, clientRefs, ct);
                return;
            }

            // Step 2: A saved PowerFlows account ("Connect new account"). Resolve it server-side
            // inside the account's own realm, as the account's token owner — the editor must show
            // the schema the step will actually run against, never the owner tenant's.
            if (_connectionScopeResolver != null)
            {
                Connections.Common.ConnectionScope? accountScope = null;
                bool accountUnavailable = false;
                try
                {
                    accountScope = await _connectionScopeResolver.TryResolveAsync(connGuid, ct);
                }
                catch (UnauthorizedActionException)
                {
                    // It IS a saved account, but its credential can no longer be honoured.
                    accountUnavailable = true;
                }

                if (accountUnavailable)
                {
                    clientRefs.Add(new PipelineEditorClientRef
                    {
                        ConnectionPublicId = connStr,
                        AppPublicId = tableRef.AppPublicId,
                        TablePublicId = tableRef.TablePublicId,
                        Reason = PipelineEditorRefReason.ConnectionUnavailable
                    });
                    return;
                }

                if (accountScope != null)
                {
                    await ResolveSavedAccountTableAsync(connStr, accountScope, tableRef, editorTables, clientRefs, ct);
                    return;
                }
            }

            // Step 3: Check if it is a SavedConnection (PipelineConnection entity)
            var savedConn = await _pipelineRepo.GetConnectionByPublicIdAsync(connGuid, ct);
            if (savedConn != null)
            {
                // SavedConnection targets an external API (Quickbase realm).
                // The backend cannot query this API's field schema without implementing
                // an outbound HTTP call using CredentialsJson — which is out of scope
                // for this authoritative read-only endpoint. Return as clientResolveRef.
                clientRefs.Add(new PipelineEditorClientRef
                {
                    ConnectionPublicId = connStr,
                    AppPublicId = tableRef.AppPublicId,
                    TablePublicId = tableRef.TablePublicId,
                    Reason = PipelineEditorRefReason.SavedConnection
                });
                return;
            }

            // Unknown GUID — neither a tenant nor a saved connection
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connStr,
                AppPublicId = tableRef.AppPublicId,
                TablePublicId = tableRef.TablePublicId,
                Reason = PipelineEditorRefReason.TenantNotFound
            });
            return;
        }

        // ── Current-tenant table (no connection or empty connection) ─────────────
        await ResolveCurrentTenantTableAsync(
            string.Empty, tableRef.TablePublicId, editorTables, clientRefs, ct);
    }

    private async Task ResolveCurrentTenantTableAsync(
        string connectionPublicId,
        Guid tablePublicId,
        List<PipelineEditorTableMetadata> editorTables,
        List<PipelineEditorClientRef> clientRefs,
        CancellationToken ct)
    {
        try
        {
            var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
            var appPublicId = await _appRepo.GetPublicIdByIdAsync(table.AppId, ct);
            var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

            editorTables.Add(BuildTableMetadata(connectionPublicId, appPublicId, table, fields));
        }
        catch (NotFoundException)
        {
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connectionPublicId,
                TablePublicId = tablePublicId,
                Reason = PipelineEditorRefReason.TableNotFound
            });
        }
        catch (UnauthorizedActionException)
        {
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connectionPublicId,
                TablePublicId = tablePublicId,
                Reason = PipelineEditorRefReason.AccessDenied
            });
        }
        catch
        {
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connectionPublicId,
                TablePublicId = tablePublicId,
                Reason = PipelineEditorRefReason.ResolutionError
            });
        }
    }

    private async Task ResolveCrossTenantTableAsync(
        string connectionPublicId,
        long targetTenantId,
        TableRefInfo tableRef,
        List<PipelineEditorTableMetadata> editorTables,
        List<PipelineEditorClientRef> clientRefs,
        CancellationToken ct)
    {
        // Create a scoped DI container for the target tenant — same pattern as PipelineEngine.
        // The scoped IQueryContext is set to the target tenant's internal ID so that all
        // tenant-scoped repositories within this scope operate against the target tenant's DB.
        using var scope = _scopeFactory.CreateScope();
        var scopedQueryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();
        scopedQueryContext.SetTenantId(targetTenantId);
        // Copy user identity from the request context
        scopedQueryContext.SetUserIdentity(
            _queryContext.UserId,
            _queryContext.IsSuperAdmin,
            _queryContext.UserName,
            _queryContext.UserEmail,
            _queryContext.Permissions,
            _queryContext.TenantRole);

        var scopedTenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        var scopedTableRepo = scope.ServiceProvider.GetRequiredService<IAppTableRepository>();
        var scopedFieldRepo = scope.ServiceProvider.GetRequiredService<IAppFieldRepository>();
        var scopedAppRepo = scope.ServiceProvider.GetRequiredService<IAppRepository>();

        try
        {
            // Verify the user is an active member of the target tenant
            var memberCheck = await scopedTenantRepo.IsActiveMemberAsync(_queryContext.UserId, ct);
            if (!memberCheck)
            {
                clientRefs.Add(new PipelineEditorClientRef
                {
                    ConnectionPublicId = connectionPublicId,
                    AppPublicId = tableRef.AppPublicId,
                    TablePublicId = tableRef.TablePublicId,
                    Reason = PipelineEditorRefReason.AccessDenied
                });
                return;
            }

            var table = await scopedTableRepo.GetByPublicIdAsync(tableRef.TablePublicId, ct);
            var appPublicId = await scopedAppRepo.GetPublicIdByIdAsync(table.AppId, ct);
            var fields = await scopedFieldRepo.ListByTableAsync(table.Id, ct);

            editorTables.Add(BuildTableMetadata(connectionPublicId, appPublicId, table, fields));
        }
        catch (NotFoundException)
        {
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connectionPublicId,
                AppPublicId = tableRef.AppPublicId,
                TablePublicId = tableRef.TablePublicId,
                Reason = PipelineEditorRefReason.TableNotFound
            });
        }
        catch (UnauthorizedActionException)
        {
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connectionPublicId,
                AppPublicId = tableRef.AppPublicId,
                TablePublicId = tableRef.TablePublicId,
                Reason = PipelineEditorRefReason.AccessDenied
            });
        }
        catch
        {
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connectionPublicId,
                AppPublicId = tableRef.AppPublicId,
                TablePublicId = tableRef.TablePublicId,
                Reason = PipelineEditorRefReason.ResolutionError
            });
        }
    }

    /// <summary>
    /// Resolves a table through a saved PowerFlows account. Runs inside the account's realm with
    /// the token owner's identity and the token's app restrictions, so what the editor shows is
    /// exactly what the step will be able to touch at runtime.
    /// </summary>
    private async Task ResolveSavedAccountTableAsync(
        string connectionPublicId,
        Connections.Common.ConnectionScope accountScope,
        TableRefInfo tableRef,
        List<PipelineEditorTableMetadata> editorTables,
        List<PipelineEditorClientRef> clientRefs,
        CancellationToken ct)
    {
        try
        {
            await using var targetScope = await Connections.Common.TargetTenantScopeHelper.OpenAsync(_scopeFactory, accountScope, ct);

            var scopedTableRepo = targetScope.GetRequiredService<IAppTableRepository>();
            var scopedFieldRepo = targetScope.GetRequiredService<IAppFieldRepository>();
            var scopedAppRepo = targetScope.GetRequiredService<IAppRepository>();

            var table = await scopedTableRepo.GetByPublicIdAsync(tableRef.TablePublicId, ct);

            // Honour the token's app restrictions — a restricted token must not reveal
            // schema for an app it cannot reach.
            if (!accountScope.TokenAccessAllApps && !accountScope.AllowedAppIds.Contains(table.AppId))
            {
                clientRefs.Add(new PipelineEditorClientRef
                {
                    ConnectionPublicId = connectionPublicId,
                    AppPublicId = tableRef.AppPublicId,
                    TablePublicId = tableRef.TablePublicId,
                    Reason = PipelineEditorRefReason.AccessDenied
                });
                return;
            }

            var appPublicId = await scopedAppRepo.GetPublicIdByIdAsync(table.AppId, ct);
            var fields = await scopedFieldRepo.ListByTableAsync(table.Id, ct);

            editorTables.Add(BuildTableMetadata(connectionPublicId, appPublicId, table, fields));
        }
        catch (NotFoundException)
        {
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connectionPublicId,
                AppPublicId = tableRef.AppPublicId,
                TablePublicId = tableRef.TablePublicId,
                Reason = PipelineEditorRefReason.TableNotFound
            });
        }
        catch (UnauthorizedActionException)
        {
            // Raised by TargetTenantScopeHelper when the token owner is no longer usable
            // in the account's realm — surfaced as "reconnect", not as owner-tenant data.
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connectionPublicId,
                AppPublicId = tableRef.AppPublicId,
                TablePublicId = tableRef.TablePublicId,
                Reason = PipelineEditorRefReason.ConnectionUnavailable
            });
        }
        catch
        {
            clientRefs.Add(new PipelineEditorClientRef
            {
                ConnectionPublicId = connectionPublicId,
                AppPublicId = tableRef.AppPublicId,
                TablePublicId = tableRef.TablePublicId,
                Reason = PipelineEditorRefReason.ResolutionError
            });
        }
    }

    private static PipelineEditorTableMetadata BuildTableMetadata(
        string connectionPublicId,
        Guid appPublicId,
        AppTable table,
        IReadOnlyList<AppField> fields)
    {
        return new PipelineEditorTableMetadata
        {
            ConnectionPublicId = connectionPublicId,
            AppPublicId = appPublicId,
            TablePublicId = table.PublicId,
            TableName = table.Name,
            Fields = fields.Select(f => new PipelineEditorFieldMetadata
            {
                PublicId = f.PublicId,
                Name = f.Name,
                Label = f.Label,
                TypeCode = f.TypeCode,
                Fid = f.Fid,
                Settings = f.Settings,
                DefaultValue = f.DefaultValue,
                IsRequired = f.IsRequired,
                IsSystem = f.IsSystem
            }).ToList()
        };
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Step tree sort (identical to GetPipelineQueryHandler)
    // ────────────────────────────────────────────────────────────────────────────

    private static void SortSteps(List<PipelineEditorStepResult> dtos)
    {
        dtos.Sort((a, b) => a.DisplayOrder.CompareTo(b.DisplayOrder));
        foreach (var dto in dtos)
        {
            if (dto.Children.Any()) SortSteps(dto.Children);
            if (dto.ElseChildren.Any()) SortSteps(dto.ElseChildren);
            if (dto.SuccessChildren.Any()) SortSteps(dto.SuccessChildren);
            if (dto.ErrorChildren.Any()) SortSteps(dto.ErrorChildren);
        }
    }
}
