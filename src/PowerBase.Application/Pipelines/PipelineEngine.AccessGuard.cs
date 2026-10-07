using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
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
