using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Runtime permission guard. A PowerFlow acts as its owner (or as the token owner of a saved
/// account), so before a record step touches a table the same app-membership and table-level
/// role rules that the Records API applies must hold for that identity. A step that fails the
/// check fails the run with a non-retryable error instead of writing data.
/// </summary>
public partial class PipelineEngine
{
    private const string StepPermissionDeniedMessage =
        "You don't have permission to perform this action. Ensure your user has the required access permissions.";

    

    // The user whose identity the current step's services run as. Set around saved-account steps, where
    // the scope's identity is the token owner; otherwise the engine's own (flow owner) identity applies.
    private readonly AsyncLocal<long?> _stepActingUserId = new();
    private long StepActingUserId => _stepActingUserId.Value ?? _queryContext.UserId;

    // The service scope the current step runs in (same tenant, another tenant, or a saved account), so
    // record-level checks inside a step use the same identity and database as the step itself.
    private readonly AsyncLocal<IServiceProvider?> _stepServices = new();
    private IServiceProvider CurrentStepServices => _stepServices.Value ?? _serviceProvider;

    private async Task<string> RunInStepScopeAsync(IServiceProvider services, long? actingUserId, Func<Task<string>> run)
    {
        _stepServices.Value = services;
        _stepActingUserId.Value = actingUserId;
        try { return await run(); }
        finally
        {
            _stepServices.Value = null;
            _stepActingUserId.Value = null;
        }
    }

    /// <summary>The role's table access for the current step, or null when the identity is unrestricted.</summary>
    private async Task<TableAccessContext?> GetRestrictedAccessAsync(AppTable table, CancellationToken ct)
    {
        try
        {
            var context = await GetStepTableAccessAsync(CurrentStepServices, table.PublicId, ct);
            return context == null || context.Access.Unrestricted ? null : context.Access;
        }
        catch (UnauthorizedActionException ex)
        {
            throw new PipelineNonRetryableException($"{StepPermissionDeniedMessage} ({ex.Message})");
        }
    }

    /// <summary>Field ids the role may read; null when the identity sees every field.</summary>
    private static HashSet<int>? ReadableFieldIds(TableAccessContext? access) =>
        access == null ? null : access.VisibleFields.Where(f => f.Fid.HasValue).Select(f => f.Fid!.Value).ToHashSet();

    /// <summary>
    /// Narrows a search to what the role may see: its record filter and, for "own records" scope, the
    /// user's own records. Fails closed on a role filter built on calculated fields, which this search
    /// path cannot evaluate, instead of silently returning everything.
    /// </summary>
    private static FilterGroup? ApplyRoleRowRestrictions(FilterGroup? tree, TableAccessContext? access, IReadOnlyList<AppField> fields)
    {
        if (access == null || (access.ViewFilter == null && !access.RestrictToCreatedBy.HasValue)) return tree;

        var computed = fields.Where(f => f.Fid.HasValue && PhysicalNaming.IsComputedTypeCode(f.TypeCode))
            .Select(f => (long)f.Fid!.Value).ToHashSet();
        if (access.ViewFilter != null && computed.Count > 0 &&
            Formulas.FormulaFilterSorter.TreeContainsFormulaField(access.ViewFilter, computed))
            throw new PipelineNonRetryableException(
                "The role's record filter uses calculated fields, which a PowerFlow search cannot apply. Ask an administrator to adjust the role.");

        var combined = new FilterGroup { Logic = "and", Nodes = new List<FilterNode>() };
        if (tree != null) combined.Nodes.Add(new FilterNode { Group = tree });
        if (access.ViewFilter != null) combined.Nodes.Add(new FilterNode { Group = access.ViewFilter });
        if (access.RestrictToCreatedBy.HasValue)
            combined.Nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = 4, Operator = "eq", Value = access.RestrictToCreatedBy.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
        return combined;
    }

    /// <summary>
    /// Record-level rules for a step that reads or changes ONE record: "own records" scope and the role's
    /// record filter, plus (for writes) that every written field is editable for the role.
    /// </summary>
    private async Task EnforceRecordAccessAsync(IRecordRepository recordRepo, AppTable table, IReadOnlyList<AppField> fields,
        Guid recordPublicId, PipelineRecordAccessKind kind, IEnumerable<AppField>? writtenFields, CancellationToken ct)
    {
        var access = await GetRestrictedAccessAsync(table, ct);
        if (access == null) return;

        try
        {
            if (!PipelineRecordAccess.IsAllowed(access, kind))
                throw new UnauthorizedActionException(PipelineRecordAccess.Describe(kind, table.Name));

            if (kind is PipelineRecordAccessKind.Modify or PipelineRecordAccessKind.Delete
                && (access.ViewScope == RecordScopes.OwnRecords || access.ModifyScope == RecordScopes.OwnRecords))
            {
                var enforcer = CurrentStepServices.GetService<IRolePermissionEnforcer>();
                if (enforcer != null) await enforcer.EnsureRecordOwnedAsync(table, recordPublicId, ct);
            }

            if ((access.ViewFilter != null || access.RestrictToCreatedBy.HasValue) &&
                !await recordRepo.ExistsWithViewFilterAsync(table, fields, recordPublicId, access.ViewFilter, access.RestrictToCreatedBy, ct))
                throw new UnauthorizedActionException($"The record is not accessible to this user in table '{table.Name}'.");

            if (writtenFields != null)
                foreach (var field in writtenFields)
                    if (field.Fid != null && !access.EditableFieldIds.Contains(field.Fid.Value))
                        throw new UnauthorizedActionException(
                            $"You do not have permission to write to the field '{(!string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name)}' in table '{table.Name}'.");
        }
        catch (UnauthorizedActionException ex)
        {
            _logger.LogWarning("Pipeline record access denied on table {TableId}: {Reason}", table.Id, ex.Message);
            throw new PipelineNonRetryableException($"{StepPermissionDeniedMessage} ({ex.Message})");
        }
    }

    private sealed record StepTableAccess(AppTable Table, IReadOnlyList<AppField> Fields, TableAccessContext Access);

    // Successful lookups only, keyed by the DI scope (identity + tenant) so loops do not repeat them.
    private readonly ConcurrentDictionary<(IServiceProvider Scope, Guid TableId), StepTableAccess> _stepAccessCache = new();

    private static PipelineRecordAccessKind? RequiredAccess(PipelineStep step) => step.Type switch
    {
        "action" or "query" => step.Subtype switch
        {
            "create-record" => PipelineRecordAccessKind.Add,
            "update-record" => PipelineRecordAccessKind.Modify,
            "delete-record" => PipelineRecordAccessKind.Delete,
            "search-records" or "look-up-record" => PipelineRecordAccessKind.View,
            "prepare-bulk-upsert" or "add-bulk-upsert-row" or "commit-upsert" => PipelineRecordAccessKind.AddAndModify,
            _ => null
        },
        // A trigger reads its source table, so the flow owner must be able to view it.
        "trigger" => step.Subtype is "new-event" or "new-bulk-event" or "record-added" or "record-updated" or "record-deleted"
            ? PipelineRecordAccessKind.View : null,
        _ => null
    };

    private static Guid? ReadStepTableId(JsonElement root)
    {
        foreach (var name in new[] { "tableId", "tablePublicId", "tableLabel", "table" })
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.String)
                    continue;
                var text = property.Value.GetString();
                if (Guid.TryParse((text ?? string.Empty).Split(':').Last(), out var id)) return id;
            }
        }
        return null;
    }

    /// <summary>
    /// Enforces the access the step needs on its table for the identity behind <paramref name="services"/>.
    /// </summary>
    private async Task EnforceStepAccessAsync(PipelineStep step, IServiceProvider services, CancellationToken ct)
    {
        var requiredOrNull = RequiredAccess(step);
        if (requiredOrNull == null || string.IsNullOrWhiteSpace(step.ConfigJson)) return;
        var required = requiredOrNull.Value;

        JsonDocument document;
        try { document = JsonDocument.Parse(step.ConfigJson); }
        catch (JsonException) { return; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            var tableId = ReadStepTableId(document.RootElement);
            if (tableId == null) return; // A missing table is reported by the step itself.

            try
            {
                var context = await GetStepTableAccessAsync(services, tableId.Value, ct);
                if (context == null) return; // Access services are not available in this scope (test doubles).
                var access = context.Access;
                if (access.Unrestricted) return;

                if (!PipelineRecordAccess.IsAllowed(access, required))
                    throw new UnauthorizedActionException(PipelineRecordAccess.Describe(required, context.Table.Name));

                // A bulk upsert updates existing records matched by key, so it cannot check each one against
                // the role's row rules; fail closed for roles that have such rules.
                if (required == PipelineRecordAccessKind.AddAndModify &&
                    (access.ViewFilter != null || access.ViewScope == RecordScopes.OwnRecords || access.ModifyScope == RecordScopes.OwnRecords))
                    throw new UnauthorizedActionException(
                        $"Bulk upsert is not available to roles with record-level restrictions on table '{context.Table.Name}'.");

                if (required is PipelineRecordAccessKind.Add or PipelineRecordAccessKind.Modify
                    && document.RootElement.TryGetProperty("fieldMappings", out var mappings) && mappings.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mapping in mappings.EnumerateArray())
                    {
                        if (mapping.ValueKind != JsonValueKind.Object || !mapping.TryGetProperty("field", out var fieldRef)
                            || fieldRef.ValueKind != JsonValueKind.String) continue;
                        var field = ResolvePipelineField(fieldRef.GetString(), context.Fields);
                        if (field?.Fid != null && !access.EditableFieldIds.Contains(field.Fid.Value))
                            throw new UnauthorizedActionException(
                                $"You do not have permission to write to the field '{(!string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name)}' in table '{context.Table.Name}'.");
                    }
                }
            }
            catch (UnauthorizedActionException ex)
            {
                _logger.LogWarning("Pipeline step {StepId} ({Subtype}) denied by role permissions: {Reason}", step.Id, step.Subtype, ex.Message);
                throw new PipelineNonRetryableException($"{StepPermissionDeniedMessage} ({ex.Message})");
            }
        }
    }

    private async Task<StepTableAccess?> GetStepTableAccessAsync(IServiceProvider services, Guid tableId, CancellationToken ct)
    {
        if (_stepAccessCache.TryGetValue((services, tableId), out var cached)) return cached;

        // Membership first: the enforcer treats a non-member as unrestricted, which is right for
        // its own callers (they are gated by membership) but never for a pipeline step.
        var accessService = services.GetService<IAppAccessService>();
        var tableRepo = services.GetService<IAppTableRepository>();
        var fieldRepo = services.GetService<IAppFieldRepository>();
        var enforcer = services.GetService<IRolePermissionEnforcer>();
        if (accessService == null || tableRepo == null || fieldRepo == null || enforcer == null) return null;

        await accessService.RequireMembershipByTablePublicIdAsync(tableId, ct);

        var table = await tableRepo.GetByPublicIdAsync(tableId, ct);
        var fields = await fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await enforcer.GetTableAccessAsync(table, fields, ct);

        var result = new StepTableAccess(table, fields, access);
        _stepAccessCache[(services, tableId)] = result;
        return result;
    }
}
