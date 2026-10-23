using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Access;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Mapping;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.ConnectedTables.Sync;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Reports;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.ConnectedTables.Handlers;

/// <summary>One destination field and the source field (by Fid) that fills it.</summary>
public sealed record ConnectExistingFieldMapping(string DestinationFieldName, int SourceFid);

/// <summary>Turns a table that already exists (made by an app import) into a connected table of a table in another app of the same realm.</summary>
public sealed record ConnectExistingTableCommand(
    Guid DestinationTablePublicId, Guid SourceAppPublicId, Guid SourceTablePublicId, IReadOnlyList<ConnectExistingFieldMapping> Fields,
    string UpdateMode, ConnectedSchedule? Schedule, bool NotifyOnFailure, FilterGroup? Filter);

public sealed record ConnectExistingTableResult(Guid ConnectedTablePublicId, int FieldsConnected, IReadOnlyList<string> Notes);

/// <summary>
/// "Convert in place": the same module rows as <see cref="ConnectedTableHandlers.CreateAsync"/> writes, but for a destination table and
/// destination fields that already exist (an app import made them with the source file's field numbers, and forms and reports already
/// point at them). The connection is a same-realm connection run as the importing user, so no token is needed. Nothing here edits the
/// module's other handlers.
/// </summary>
public sealed class ConnectExistingTableHandler(
    IConnectedTableRepository connectedTables,
    IConnectedConnectionRepository connections,
    IConnectedControlRepository control,
    ConnectedAccessVerifier verifier,
    ConnectedRunStarter starter,
    ConnectionHandlers connectionHandlers,
    CreateFieldCommandHandler createField,
    IAppTableRepository appTables,
    IAppFieldRepository appFields,
    IAuditRepository audit,
    IQueryContext context,
    ILogger<ConnectExistingTableHandler> logger)
{
    public async Task<ConnectExistingTableResult> ConnectAsync(ConnectExistingTableCommand command, CancellationToken ct)
    {
        var notes = new List<string>();
        var destination = await appTables.GetByPublicIdAsync(command.DestinationTablePublicId, ct);

        var connection = (await connections.ListByOwnerAsync(context.UserId, ct))
            .FirstOrDefault(c => !c.IsDeleted && c.AuthMode == ConnectedAuthModes.SameRealmUser && c.SourceAppPublicId == command.SourceAppPublicId);
        if (connection is null)
        {
            var created = await connectionHandlers.CreateAsync(new CreateConnectionCommand(
                "Same realm (import)", ConnectedAuthModes.SameRealmUser, null, command.SourceAppPublicId, null, null), ct);
            connection = await connectionHandlers.GetOwnedAsync(created.PublicId, ct);
        }

        await using var access = await verifier.OpenAsync(connection, context.TenantId, ct);
        var source = await verifier.GetSourceTableAsync(access, command.SourceTablePublicId, ct);

        var destinationFields = (await appFields.ListByTableAsync(destination.Id, ct)).Where(f => !f.IsDeleted).ToList();
        AppField? FindDestination(string name) =>
            destinationFields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? destinationFields.FirstOrDefault(f => string.Equals(f.Label, name, StringComparison.OrdinalIgnoreCase));

        var mappings = new List<ConnectedField>();
        var order = 0;
        foreach (var map in command.Fields)
        {
            var target = FindDestination(map.DestinationFieldName);
            var sourceField = source.FindVisible(map.SourceFid);
            if (target is null || sourceField is null)
            {
                notes.Add(target is null
                    ? $"'{map.DestinationFieldName}' does not exist on the table; not connected."
                    : $"Source field {map.SourceFid} is not available in '{source.Table.Name}'; '{map.DestinationFieldName}' left as an ordinary field.");
                continue;
            }
            var plan = ConnectedTypeMapper.Plan(sourceField, access.IsCrossTenant);
            if (!plan.Connectable)
            {
                notes.Add($"'{map.DestinationFieldName}' not connected: {plan.Reason}");
                continue;
            }
            mappings.Add(new ConnectedField
            {
                AppFieldId = target.Id,
                SourceFid = map.SourceFid,
                SourceFieldName = (sourceField.Label ?? sourceField.Name).Trim(),
                SourceTypeCode = sourceField.TypeCode,
                Conversion = plan.Conversion,
                SortOrder = order++,
                State = ConnectedFieldStates.Connected,
            });
        }

        // The refresh key is the source's Record ID#; when the file did not map it to a field, add the module's usual extra field for it.
        if (mappings.All(m => m.SourceFid != ConnectedSystemFids.RecordId))
        {
            var recordId = source.FindVisible(ConnectedSystemFids.RecordId);
            if (recordId is not null)
            {
                var plan = ConnectedTypeMapper.Plan(recordId, access.IsCrossTenant);
                var used = destinationFields.Select(f => (f.Label ?? f.Name).Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var label = "Source Record ID#";
                for (var n = 2; used.Contains(label); n++) label = $"Source Record ID# ({n})";
                var created = await createField.HandleAsync(new CreateFieldCommand(destination.PublicId, plan.DestinationTypeCode!, label,
                    "Connected from \"Record ID#\".", IsRequired: false, IsAuditable: false, Settings: plan.Settings), ct);
                mappings.Add(new ConnectedField
                {
                    AppFieldId = created.Id,
                    SourceFid = ConnectedSystemFids.RecordId,
                    SourceFieldName = (recordId.Label ?? recordId.Name).Trim(),
                    SourceTypeCode = recordId.TypeCode,
                    Conversion = plan.Conversion,
                    SortOrder = order++,
                    State = ConnectedFieldStates.Connected,
                });
            }
        }

        if (mappings.Count == 0)
            throw new InvalidOperationException("None of the table's fields could be connected to the source.");

        var filter = command.Filter is { Nodes.Count: > 0 } ? ConnectedFilters.Validate(command.Filter, source) : null;
        var schedule = command.Schedule is null ? null : ConnectedScheduling.Validate(command.Schedule, null);
        const string timeZone = "UTC";

        var entity = new ConnectedTable
        {
            AppId = destination.AppId,
            AppTableId = destination.Id,
            ConnectionId = connection.Id,
            OwnerUserId = context.UserId,
            SourceTenantId = connection.SourceTenantId,
            SourceAppPublicId = connection.SourceAppPublicId,
            SourceTablePublicId = source.Table.PublicId,
            SourceTableName = source.Table.Name,
            IsCrossTenant = access.IsCrossTenant,
            RefreshKeySourceFid = ConnectedSystemFids.RecordId,
            RefreshKeyIsAuto = true,
            UpdateMode = command.UpdateMode,
            FilterEnabled = filter is not null,
            FilterJson = filter is null ? null : System.Text.Json.JsonSerializer.Serialize(filter),
            ScheduleJson = schedule is null ? null : ConnectedScheduling.Serialize(schedule),
            NextRunOn = ConnectedScheduling.NextRun(schedule, timeZone, DateTime.UtcNow),
            TimeZone = timeZone,
            NotifyOnFailure = command.NotifyOnFailure,
            MassDeleteGuardPct = 50,
            Status = ConnectedTableStatuses.Active,
            CreatedBy = context.UserId,
        };
        (entity.Id, entity.PublicId) = await connectedTables.CreateAsync(entity, mappings, ct);
        ConnectedTableCache.Invalidate(context.TenantId);
        await control.UpsertLinkAsync(new ConnectedLink
        {
            ConsumerTenantId = context.TenantId,
            ConsumerConnectedTablePublicId = entity.PublicId,
            ConsumerTableName = destination.Name,
            ProviderTenantId = entity.SourceTenantId,
            ProviderAppPublicId = entity.SourceAppPublicId,
            ProviderTablePublicId = entity.SourceTablePublicId,
            ProviderTableName = source.Table.Name,
            RunAsUserId = connection.RunAsUserId,
            Status = ConnectedLinkStatuses.Active,
        }, ct);
        await audit.LogActivityAsync("ConnectedTableCreated", "ConnectedTable", entity.PublicId.ToString(),
            $"{destination.Name} connected to {source.Table.Name} ({access.App.Name}) by app import", destination.AppId, ct: ct);

        try { await starter.QueueAsync(entity, ConnectedRunTriggers.Manual, context.UserId, false, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The first refresh of connected table {Id} could not be queued.", entity.PublicId);
            notes.Add("The first refresh could not be queued; refresh the table by hand.");
        }

        return new ConnectExistingTableResult(entity.PublicId, mappings.Count, notes);
    }
}
