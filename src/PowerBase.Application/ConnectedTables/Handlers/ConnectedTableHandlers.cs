using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Services;
using PowerBase.Application.ConnectedTables.Access;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Mapping;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.ConnectedTables.Sync;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Commands.DeleteField;
using PowerBase.Application.Reports;
using PowerBase.Application.Tables.Commands.CreateTable;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using ValidationException = PowerBase.Domain.Exceptions.ValidationException;

namespace PowerBase.Application.ConnectedTables.Handlers;

public sealed record CreateConnectedTableCommand(
    Guid AppPublicId, string Name, string? SingularLabel, string? PluralLabel, string? Description, string? Icon,
    Guid ConnectionPublicId, Guid SourceTablePublicId, IReadOnlyList<int> SourceFids, int? RefreshKeySourceFid, string UpdateMode,
    bool FilterEnabled, FilterGroup? Filter, ConnectedSchedule? Schedule, string? TimeZone, bool NotifyOnFailure);

public sealed record UpdateConnectedFilterCommand(Guid PublicId, bool FilterEnabled, FilterGroup? Filter, string RowVersion);

public sealed record ConnectFieldsCommand(Guid PublicId, IReadOnlyList<int> SourceFids, string RowVersion);

public sealed record DisconnectFieldCommand(Guid PublicId, Guid FieldPublicId, bool DeleteField, string RowVersion);

public sealed record UpdateRefreshOptionsCommand(
    Guid PublicId, string UpdateMode, ConnectedSchedule? Schedule, string? TimeZone, bool NotifyOnFailure, int MassDeleteGuardPct,
    bool ConfirmModeChange, string RowVersion);

public sealed record ChangeRefreshKeyCommand(Guid PublicId, int SourceFid, bool Confirm, string RowVersion);

public sealed record SwitchConnectionCommand(Guid PublicId, Guid ConnectionPublicId, string RowVersion);

public sealed class CreateConnectedTableCommandValidator : AbstractValidator<CreateConnectedTableCommand>
{
    public CreateConnectedTableCommandValidator()
    {
        RuleFor(c => c.AppPublicId).NotEmpty();
        RuleFor(c => c.Name).NotEmpty().WithMessage("Name the table.").MaximumLength(100);
        RuleFor(c => c.SingularLabel).MaximumLength(100);
        RuleFor(c => c.PluralLabel).MaximumLength(100);
        RuleFor(c => c.Description).MaximumLength(1000);
        RuleFor(c => c.ConnectionPublicId).NotEmpty().WithMessage("Choose a connection.");
        RuleFor(c => c.SourceTablePublicId).NotEmpty().WithMessage("Choose the records to connect.");
        RuleFor(c => c.SourceFids).NotEmpty().WithMessage("Connect at least one field.")
            .Must(f => f.Distinct().Count() == f.Count).WithMessage("A field can be connected only once.");
        RuleFor(c => c.UpdateMode).Must(m => ConnectedUpdateModes.All.Contains(m)).WithMessage("Choose how refreshes update the table.");
        RuleFor(c => c.TimeZone).Must(tz => tz is null || ConnectedScheduling.TryResolveTimeZone(tz, out _)).WithMessage("The time zone is not recognised.");
    }
}

public sealed class UpdateRefreshOptionsCommandValidator : AbstractValidator<UpdateRefreshOptionsCommand>
{
    public UpdateRefreshOptionsCommandValidator()
    {
        RuleFor(c => c.UpdateMode).Must(m => ConnectedUpdateModes.All.Contains(m)).WithMessage("Choose how refreshes update the table.");
        RuleFor(c => c.TimeZone).Must(tz => tz is null || ConnectedScheduling.TryResolveTimeZone(tz, out _)).WithMessage("The time zone is not recognised.");
        RuleFor(c => c.MassDeleteGuardPct).InclusiveBetween(1, 100);
        RuleFor(c => c.RowVersion).NotEmpty();
    }
}

/// <summary>Connected-table definitions: create (through the existing table and field handlers, so the schema engine stays the
/// only DDL writer), read, and change. The destination is an ordinary table; only the module's own rows say it is connected.</summary>
public sealed class ConnectedTableHandlers(
    IConnectedTableRepository connectedTables,
    IConnectedConnectionRepository connections,
    IConnectedRunRepository runs,
    IConnectedControlRepository control,
    IConnectedDataWriter writer,
    ConnectedAccessVerifier verifier,
    ConnectedPermissions permissions,
    ConnectedRunStarter starter,
    ConnectionHandlers connectionHandlers,
    CreateTableCommandHandler createTable,
    CreateFieldCommandHandler createField,
    DeleteTableCommandHandler deleteTable,
    DeleteFieldCommandHandler deleteField,
    IAppRepository apps,
    IAppTableRepository appTables,
    IAppFieldRepository appFields,
    IAppRoleRepository appRoles,
    IRecordRepository records,
    IUserRepository users,
    IAuditRepository audit,
    IQueryContext context,
    IOptions<ConnectedTablesOptions> options,
    ILogger<ConnectedTableHandlers> logger)
{
    /// <summary>Field types that make a sensible refresh key: one plain value per record.</summary>
    private static readonly HashSet<string> KeyTypes = new(StringComparer.Ordinal)
    {
        "Text", "Number", "Currency", "Percent", "Rating", "Email", "Phone", "Url", "Date", "DateTime", "SingleSelect", "Reference",
    };

    public async Task<ConnectedTableResult> CreateAsync(CreateConnectedTableCommand command, CancellationToken ct)
    {
        ConnectedValidation.Validate(new CreateConnectedTableCommandValidator(), command);
        var app = await apps.GetByPublicIdAsync(command.AppPublicId, ct);
        await permissions.RequireCanCreateTablesAsync(app.Id, ct);
        var name = command.Name.Trim();
        if (await appTables.NameExistsInAppAsync(app.Id, name, ct)) throw new DuplicateException("Table", "name", name);
        if (command.SourceFids.Count > options.Value.MaxConnectedFields)
            throw Invalid("SourceFids", $"A connected table can have at most {options.Value.MaxConnectedFields} connected fields.");

        var connection = await connectionHandlers.GetOwnedAsync(command.ConnectionPublicId, ct);
        await using var access = await verifier.OpenAsync(connection, context.TenantId, ct);
        var source = await verifier.GetSourceTableAsync(access, command.SourceTablePublicId, ct);
        var planned = PlanFields(source, access, command.SourceFids);

        var keyFid = command.RefreshKeySourceFid ?? ConnectedSystemFids.RecordId;
        if (keyFid != ConnectedSystemFids.RecordId) RequireKeyable(planned, keyFid);
        var filter = command.FilterEnabled ? ConnectedFilters.Validate(command.Filter, source) : null;
        var schedule = command.Schedule is null ? null : ConnectedScheduling.Validate(command.Schedule, null);
        var timeZone = string.IsNullOrWhiteSpace(command.TimeZone) ? "UTC" : command.TimeZone.Trim();

        var created = await createTable.HandleAsync(new CreateTableCommand(app.PublicId, name, command.SingularLabel, command.PluralLabel,
            string.IsNullOrWhiteSpace(command.Description) ? $"Connected to {source.Table.Name} in {access.App.Name}." : command.Description, command.Icon), ct);
        ConnectedTable entity;
        try
        {
            var destination = await appTables.GetByPublicIdAsync(created.PublicId, ct);
            var mappings = await CreateDestinationFieldsAsync(destination, planned, 0, ct);
            entity = new ConnectedTable
            {
                AppId = app.Id,
                AppTableId = destination.Id,
                ConnectionId = connection.Id,
                OwnerUserId = context.UserId,
                SourceTenantId = connection.SourceTenantId,
                SourceAppPublicId = connection.SourceAppPublicId,
                SourceTablePublicId = source.Table.PublicId,
                SourceTableName = source.Table.Name,
                IsCrossTenant = access.IsCrossTenant,
                RefreshKeySourceFid = keyFid,
                RefreshKeyIsAuto = keyFid == ConnectedSystemFids.RecordId,
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
            await control.UpsertLinkAsync(LinkOf(entity, destination.Name, source.Table.Name, connection.RunAsUserId), ct);
        }
        catch
        {
            // Nothing half-made is left behind: the table (and the fields already added to it) is removed again.
            try { await deleteTable.HandleAsync(new DeleteTableCommand(created.PublicId), CancellationToken.None); }
            catch (Exception cleanup) { logger.LogError(cleanup, "Could not remove table {Table} after a failed connected-table create.", created.PublicId); }
            throw;
        }

        await audit.LogActivityAsync("ConnectedTableCreated", "ConnectedTable", entity.PublicId.ToString(),
            $"{name} connected to {source.Table.Name} ({access.App.Name})", app.Id, ct: ct);
        try { await starter.QueueAsync(entity, ConnectedRunTriggers.Manual, context.UserId, false, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "The first refresh of connected table {Id} could not be queued.", entity.PublicId); }
        return await GetAsync(entity.PublicId, ct);
    }

    public async Task<ConnectedTableResult> GetAsync(Guid publicId, CancellationToken ct)
    {
        var table = await GetTableAsync(publicId, ct);
        await permissions.RequireMemberAsync(table.AppId, ct);
        return await BuildResultAsync(table, ct);
    }

    public async Task<IReadOnlyList<ConnectedTableSummaryResult>> ListByAppAsync(Guid appPublicId, CancellationToken ct)
    {
        var app = await apps.GetByPublicIdAsync(appPublicId, ct);
        await permissions.RequireMemberAsync(app.Id, ct);
        var list = await connectedTables.ListByAppAsync(app.Id, ct);
        var destinations = (await appTables.ListByAppAsync(app.Id, ct)).ToDictionary(t => t.Id);
        var realms = await control.GetTenantNamesAsync(list.Select(t => t.SourceTenantId).ToList(), ct);
        var results = new List<ConnectedTableSummaryResult>(list.Count);
        foreach (var t in list)
        {
            if (!destinations.TryGetValue(t.AppTableId, out var destination)) continue;
            results.Add(await SummaryAsync(t, destination, realms, ct));
        }
        return results;
    }

    /// <summary>Which tables of an app are connected, and which of their fields are connected fields: drives the plug icons
    /// in the table bar and in field lists.</summary>
    public async Task<ConnectedMarkersResult> MarkersAsync(Guid appPublicId, CancellationToken ct)
    {
        var app = await apps.GetByPublicIdAsync(appPublicId, ct);
        await permissions.RequireMemberAsync(app.Id, ct);
        var list = await connectedTables.ListByAppAsync(app.Id, ct);
        if (list.Count == 0) return new ConnectedMarkersResult([], [], []);
        var destinations = (await appTables.ListByAppAsync(app.Id, ct)).ToDictionary(t => t.Id);
        var tables = new List<Guid>();
        var fields = new List<Guid>();
        var tableFields = new List<ConnectedTableFidsResult>();
        foreach (var t in list)
        {
            if (!destinations.TryGetValue(t.AppTableId, out var destination)) continue;
            tables.Add(destination.PublicId);
            var connectedIds = (await connectedTables.ListFieldsAsync(t.Id, ct)).Select(f => f.AppFieldId).ToHashSet();
            var connected = (await appFields.ListByTableAsync(destination.Id, ct) ?? Array.Empty<AppField>())
                .Where(f => connectedIds.Contains(f.Id) && !f.IsDeleted).ToList();
            fields.AddRange(connected.Select(f => f.PublicId));
            tableFields.Add(new ConnectedTableFidsResult(destination.PublicId, connected.Where(f => f.Fid.HasValue).Select(f => f.Fid!.Value).ToList()));
        }
        return new ConnectedMarkersResult(tables, fields, tableFields);
    }

    /// <summary>The connected-table summary of an ordinary table, or null when it isn't connected.</summary>
    public async Task<ConnectedTableSummaryResult?> GetByTableAsync(Guid tablePublicId, CancellationToken ct)
    {
        var destination = await appTables.GetByPublicIdAsync(tablePublicId, ct);
        await permissions.RequireMemberAsync(destination.AppId, ct);
        var table = await connectedTables.GetByAppTableIdAsync(destination.Id, ct);
        if (table is null) return null;
        return await SummaryAsync(table, destination, await control.GetTenantNamesAsync([table.SourceTenantId], ct), ct);
    }

    public async Task<bool> NameExistsAsync(Guid appPublicId, string name, CancellationToken ct)
    {
        var app = await apps.GetByPublicIdAsync(appPublicId, ct);
        await permissions.RequireMemberAsync(app.Id, ct);
        return await appTables.NameExistsInAppAsync(app.Id, (name ?? string.Empty).Trim(), ct);
    }

    public async Task<ConnectedTableResult> UpdateFilterAsync(UpdateConnectedFilterCommand command, CancellationToken ct)
    {
        var table = await GetTableAsync(command.PublicId, ct);
        permissions.RequireOwner(table);
        table.RowVersion = ConnectedTokens.RowVersion(command.RowVersion);
        FilterGroup? filter = null;
        if (command.FilterEnabled)
        {
            await using var access = await OpenAsync(table, ct);
            var source = await verifier.GetSourceTableAsync(access, table.SourceTablePublicId, ct);
            filter = ConnectedFilters.Validate(command.Filter, source);
        }
        table.FilterEnabled = filter is not null;
        table.FilterJson = filter is null ? null : System.Text.Json.JsonSerializer.Serialize(filter);
        table.FullRefreshVersion++;
        await SaveAsync(table, ct);
        await audit.LogActivityAsync("ConnectedFilterChanged", "ConnectedTable", table.PublicId.ToString(), table.SourceTableName, table.AppId, ct: ct);
        return await BuildResultAsync(table, ct);
    }

    public async Task<ConnectedTableResult> ConnectFieldsAsync(ConnectFieldsCommand command, CancellationToken ct)
    {
        var table = await GetTableAsync(command.PublicId, ct);
        permissions.RequireOwner(table);
        table.RowVersion = ConnectedTokens.RowVersion(command.RowVersion);
        var existing = await connectedTables.ListFieldsAsync(table.Id, ct);
        var adding = command.SourceFids.Distinct().Where(fid => existing.All(e => e.SourceFid != fid)).ToList();
        if (adding.Count == 0) return await BuildResultAsync(table, ct);
        if (existing.Count + adding.Count > options.Value.MaxConnectedFields)
            throw Invalid("SourceFids", $"A connected table can have at most {options.Value.MaxConnectedFields} connected fields.");

        await using var access = await OpenAsync(table, ct);
        var source = await verifier.GetSourceTableAsync(access, table.SourceTablePublicId, ct);
        var planned = PlanFields(source, access, adding);
        var destination = await appTables.GetByIdAsync(table.AppTableId, ct);
        var mappings = await CreateDestinationFieldsAsync(destination, planned, existing.Count == 0 ? 0 : existing.Max(e => e.SortOrder) + 1, ct);
        await connectedTables.AddFieldsAsync(table.Id, mappings, ct);
        table.MappingVersion++;
        table.FullRefreshVersion++;
        await SaveAsync(table, ct);
        return await BuildResultAsync(table, ct);
    }

    public async Task<ConnectedTableResult> DisconnectFieldAsync(DisconnectFieldCommand command, CancellationToken ct)
    {
        var table = await GetTableAsync(command.PublicId, ct);
        permissions.RequireOwner(table);
        table.RowVersion = ConnectedTokens.RowVersion(command.RowVersion);
        var field = await appFields.GetByPublicIdAsync(command.FieldPublicId, ct);
        var mapping = field is null ? null : (await connectedTables.ListFieldsAsync(table.Id, ct)).FirstOrDefault(m => m.AppFieldId == field.Id);
        if (field is null || mapping is null) throw new NotFoundException("ConnectedField", command.FieldPublicId);
        if (table.RefreshKeySourceFid != ConnectedSystemFids.RecordId && mapping.SourceFid == table.RefreshKeySourceFid)
            throw ConnectedTableException.Conflict(ConnectedErrorCodes.Forbidden, "This field is the refresh key. Choose another refresh key before disconnecting it.");

        await connectedTables.RemoveFieldAsync(mapping.Id, ct);
        table.MappingVersion++;
        await SaveAsync(table, ct);
        if (command.DeleteField)
        {
            var destination = await appTables.GetByIdAsync(table.AppTableId, ct);
            await deleteField.HandleAsync(new DeleteFieldCommand(destination.PublicId, field.PublicId, Force: true), ct);
        }
        return await BuildResultAsync(await GetTableAsync(table.PublicId, ct), ct);
    }

    public async Task<ConnectedTableResult> UpdateRefreshOptionsAsync(UpdateRefreshOptionsCommand command, CancellationToken ct)
    {
        ConnectedValidation.Validate(new UpdateRefreshOptionsCommandValidator(), command);
        var table = await GetTableAsync(command.PublicId, ct);
        await permissions.RequireOwnerOrAdminAsync(table, ct);
        table.RowVersion = ConnectedTokens.RowVersion(command.RowVersion);

        if (table.UpdateMode == ConnectedUpdateModes.Append && command.UpdateMode == ConnectedUpdateModes.Mirror && !command.ConfirmModeChange)
            throw Invalid("UpdateMode", "Switching to \"Make my table match the source exactly\" removes the records that are no longer in the source on the next refresh. Confirm to continue.");

        var schedule = command.Schedule is null ? null : ConnectedScheduling.Validate(command.Schedule, ConnectedScheduling.Parse(table.ScheduleJson));
        table.UpdateMode = command.UpdateMode;
        table.ScheduleJson = schedule is null ? null : ConnectedScheduling.Serialize(schedule);
        table.TimeZone = string.IsNullOrWhiteSpace(command.TimeZone) ? table.TimeZone : command.TimeZone.Trim();
        table.NextRunOn = ConnectedScheduling.NextRun(schedule, table.TimeZone, DateTime.UtcNow);
        table.NotifyOnFailure = command.NotifyOnFailure;
        table.MassDeleteGuardPct = (byte)command.MassDeleteGuardPct;
        if (schedule?.IsAutomatic == true) table.ConsecutiveFailures = 0;
        await SaveAsync(table, ct);
        return await BuildResultAsync(table, ct);
    }

    /// <summary>Changes which source field identifies a record. When the new key's values are already in the table, the key map
    /// is rebuilt from them so records keep their identity (and their local fields); otherwise it is cleared and the next
    /// refresh matches records afresh.</summary>
    public async Task<ConnectedTableResult> ChangeRefreshKeyAsync(ChangeRefreshKeyCommand command, CancellationToken ct)
    {
        var table = await GetTableAsync(command.PublicId, ct);
        permissions.RequireOwner(table);
        table.RowVersion = ConnectedTokens.RowVersion(command.RowVersion);
        if (!command.Confirm)
            throw Invalid("Confirm", "Changing the refresh key can change which records the table shows. Confirm to continue.");
        if (command.SourceFid == table.RefreshKeySourceFid) return await BuildResultAsync(table, ct);

        var mappings = await connectedTables.ListFieldsAsync(table.Id, ct);
        var keyMapping = mappings.FirstOrDefault(m => m.SourceFid == command.SourceFid);
        if (command.SourceFid != ConnectedSystemFids.RecordId)
        {
            if (keyMapping is null) throw Invalid("SourceFid", "The refresh key must be one of the connected fields.");
            await using var access = await OpenAsync(table, ct);
            var source = await verifier.GetSourceTableAsync(access, table.SourceTablePublicId, ct);
            var sourceField = source.FindVisible(command.SourceFid) ?? throw Invalid("SourceFid", "That field is not available in the source.");
            if (!KeyTypes.Contains(sourceField.TypeCode) || sourceField.IsSystem && sourceField.Fid != ConnectedSystemFids.RecordId)
                throw Invalid("SourceFid", "Choose a field that holds one plain value per record (text, number, date, email…).");
        }

        var destination = await appTables.GetByIdAsync(table.AppTableId, ct);
        var keyDestination = keyMapping is null ? null : await appFields.GetByIdInTableAsync(keyMapping.AppFieldId, destination.Id, ct);
        if (keyDestination is not null) await writer.ReplaceSyncRowsAsync(table.Id, await KeysFromDestinationAsync(destination, keyDestination, ct), ct);
        else await writer.ClearSyncRowsAsync(table.Id, ct);

        table.RefreshKeySourceFid = command.SourceFid;
        table.RefreshKeyIsAuto = command.SourceFid == ConnectedSystemFids.RecordId;
        table.FullRefreshVersion++;
        await SaveAsync(table, ct);
        await audit.LogActivityAsync("ConnectedRefreshKeyChanged", "ConnectedTable", table.PublicId.ToString(), destination.Name, table.AppId, ct: ct);
        return await BuildResultAsync(table, ct);
    }

    /// <summary>An app administrator moves the table to one of their own connections (to the same source app) and becomes its owner.</summary>
    public async Task<ConnectedTableResult> SwitchConnectionAsync(SwitchConnectionCommand command, CancellationToken ct)
    {
        var table = await GetTableAsync(command.PublicId, ct);
        await permissions.RequireOwnerOrAdminAsync(table, ct);
        table.RowVersion = ConnectedTokens.RowVersion(command.RowVersion);
        var connection = await connectionHandlers.GetOwnedAsync(command.ConnectionPublicId, ct);
        if (connection.SourceAppPublicId != table.SourceAppPublicId || connection.SourceTenantId != table.SourceTenantId)
            throw Invalid("ConnectionPublicId", "Choose a connection to the same source app.");

        await using (var access = await verifier.OpenAsync(connection, context.TenantId, ct))
        {
            var source = await verifier.GetSourceTableAsync(access, table.SourceTablePublicId, ct);
            table.SourceTableName = source.Table.Name;
        }
        table.ConnectionId = connection.Id;
        table.OwnerUserId = context.UserId;
        if (table.Status is ConnectedTableStatuses.NeedsAttention) { table.Status = ConnectedTableStatuses.Active; table.AttentionReason = null; }
        await SaveAsync(table, ct);
        var destination = await appTables.GetByIdAsync(table.AppTableId, ct);
        await control.UpsertLinkAsync(LinkOf(table, destination.Name, table.SourceTableName, connection.RunAsUserId), ct);
        await audit.LogActivityAsync("ConnectionSwitched", "ConnectedTable", table.PublicId.ToString(), $"{destination.Name} → {connection.Name}", table.AppId, ct: ct);
        return await BuildResultAsync(table, ct);
    }

    public async Task<ConnectedTableResult> SetRefreshPermissionAsync(Guid publicId, IReadOnlyList<Guid> rolePublicIds, CancellationToken ct)
    {
        var table = await GetTableAsync(publicId, ct);
        await permissions.RequireAdminAsync(table.AppId, ct);
        var roles = await appRoles.ListDetailsByAppIdAsync(table.AppId, ct);
        var ids = roles.Where(r => rolePublicIds.Contains(r.PublicId)).Select(r => r.Id).ToList();
        await connectedTables.SetRefreshRoleIdsAsync(table.Id, ids, ct);
        return await BuildResultAsync(table, ct);
    }

    /// <summary>Turns the connected table into an ordinary, editable table. Its fields and records stay.</summary>
    public async Task DisconnectAsync(Guid publicId, CancellationToken ct)
    {
        var table = await GetTableAsync(publicId, ct);
        await permissions.RequireAdminAsync(table.AppId, ct);
        if (await runs.GetActiveForTableAsync(table.Id, ct) is { } active) await runs.RequestCancelAsync(active.Id, ct);
        await connectedTables.SoftDeleteAsync(table.Id, ConnectedTableStatuses.Removed, ct);
        ConnectedTableCache.Invalidate(context.TenantId);
        await writer.ClearSyncRowsAsync(table.Id, ct);
        if (await control.GetLinkAsync(context.TenantId, table.PublicId, ct) is { } link)
            await control.SetLinkStatusAsync(link.Id, ConnectedLinkStatuses.Removed, ct);
        await audit.LogActivityAsync("ConnectedTableDisconnected", "ConnectedTable", table.PublicId.ToString(), table.SourceTableName, table.AppId, ct: ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record PlannedField(AppField Source, ConnectedFieldPlan Plan);

    private static List<PlannedField> PlanFields(ConnectedSourceTable source, ConnectedSourceAccess access, IReadOnlyList<int> fids)
    {
        var planned = new List<PlannedField>(fids.Count);
        foreach (var fid in fids)
        {
            var field = source.FindVisible(fid) ?? throw Invalid("SourceFids", $"Field {fid} is not available in the source table.");
            if (field.IsEncrypted && !access.AllowsEncryptedFields)
                throw Invalid("SourceFids", $"\"{field.Label ?? field.Name}\" is encrypted, and the source realm's approval doesn't allow copying encrypted fields.");
            var plan = ConnectedTypeMapper.Plan(field, access.IsCrossTenant);
            if (!plan.Connectable) throw Invalid("SourceFids", $"\"{field.Label ?? field.Name}\": {plan.Reason}");
            planned.Add(new PlannedField(field, plan));
        }
        return planned;
    }

    private static void RequireKeyable(IReadOnlyList<PlannedField> planned, int keyFid)
    {
        var key = planned.FirstOrDefault(p => p.Source.Fid == keyFid)
            ?? throw Invalid("RefreshKeySourceFid", "The refresh key must be one of the connected fields.");
        if (!KeyTypes.Contains(key.Source.TypeCode))
            throw Invalid("RefreshKeySourceFid", "Choose a refresh key that holds one plain value per record (text, number, date, email…).");
    }

    private async Task<List<ConnectedField>> CreateDestinationFieldsAsync(AppTable destination, IReadOnlyList<PlannedField> planned, int firstSortOrder, CancellationToken ct)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in await appFields.ListByTableAsync(destination.Id, ct) ?? Array.Empty<AppField>())
            if (!f.IsDeleted) used.Add((f.Label ?? f.Name).Trim());
        foreach (var label in AppSeeder.SystemFieldLabels) used.Add(label);

        var mappings = new List<ConnectedField>(planned.Count);
        var order = firstSortOrder;
        foreach (var (sourceField, plan) in planned)
        {
            var sourceLabel = (sourceField.Label ?? sourceField.Name).Trim();
            var label = UniqueLabel(sourceField.IsSystem ? $"Source {sourceLabel}" : sourceLabel, used);
            CreateFieldResult created;
            try
            {
                created = await createField.HandleAsync(new CreateFieldCommand(destination.PublicId, plan.DestinationTypeCode!, label,
                    $"Connected from \"{sourceLabel}\".", IsRequired: false, IsAuditable: false, Settings: plan.Settings, IsEncrypted: sourceField.IsEncrypted), ct);
            }
            catch (ValidationException) when (plan.Settings is not null)
            {
                // The source settings didn't validate as destination settings: fall back to the type's defaults.
                created = await createField.HandleAsync(new CreateFieldCommand(destination.PublicId, plan.DestinationTypeCode!, label,
                    $"Connected from \"{sourceLabel}\".", IsRequired: false, IsAuditable: false, IsEncrypted: sourceField.IsEncrypted), ct);
            }
            mappings.Add(new ConnectedField
            {
                AppFieldId = created.Id,
                SourceFid = sourceField.Fid!.Value,
                SourceFieldName = sourceLabel,
                SourceTypeCode = sourceField.TypeCode,
                Conversion = plan.Conversion,
                SortOrder = order++,
                State = ConnectedFieldStates.Connected,
            });
        }
        return mappings;
    }

    private static string UniqueLabel(string label, HashSet<string> used)
    {
        if (label.Length > 90) label = label[..90];
        var candidate = label;
        for (var n = 2; used.Contains(candidate); n++) candidate = $"{label} ({n})";
        used.Add(candidate);
        return candidate;
    }

    private async Task<List<(string Key, long LocalRecordId, Guid LocalPublicId)>> KeysFromDestinationAsync(AppTable destination, AppField keyField, CancellationToken ct)
    {
        var result = new List<(string, long, Guid)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long cursor = 0;
        while (true)
        {
            var rows = await records.ListBatchAfterIdAsync(destination, [keyField], cursor, 2000, ct: ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var id = Convert.ToInt64(row["Id"]);
                cursor = id;
                if (ConnectedValues.Key(ConnectedValues.Read(keyField, row, null)) is { } key && seen.Add(key))
                    result.Add((key, id, (Guid)row["PublicId"]!));
            }
            if (rows.Count < 2000) break;
        }
        return result;
    }

    private async Task<ConnectedSourceAccess> OpenAsync(ConnectedTable table, CancellationToken ct)
    {
        var connection = await connections.GetByIdAsync(table.ConnectionId, ct)
            ?? throw ConnectedTableException.Forbidden(ConnectedErrorCodes.ConnectionUnusable, "The connection this table used was deleted. Switch the table to another connection.");
        return await verifier.OpenAsync(connection, context.TenantId, ct);
    }

    private async Task<ConnectedTable> GetTableAsync(Guid publicId, CancellationToken ct) =>
        await connectedTables.GetByPublicIdAsync(publicId, ct) ?? throw new NotFoundException("ConnectedTable", publicId);

    private async Task SaveAsync(ConnectedTable table, CancellationToken ct)
    {
        table.ModifiedBy = context.UserId;
        await connectedTables.UpdateSettingsAsync(table, ct);
        var saved = await GetTableAsync(table.PublicId, ct);
        table.RowVersion = saved.RowVersion;
    }

    private ConnectedLink LinkOf(ConnectedTable table, string destinationName, string sourceTableName, long runAsUserId) => new()
    {
        ConsumerTenantId = context.TenantId,
        ConsumerConnectedTablePublicId = table.PublicId,
        ConsumerTableName = destinationName,
        ProviderTenantId = table.SourceTenantId,
        ProviderAppPublicId = table.SourceAppPublicId,
        ProviderTablePublicId = table.SourceTablePublicId,
        ProviderTableName = sourceTableName,
        RunAsUserId = runAsUserId,
        Status = ConnectedLinkStatuses.Active,
    };

    private async Task<ConnectedTableSummaryResult> SummaryAsync(ConnectedTable t, AppTable destination, IReadOnlyDictionary<long, string> realms, CancellationToken ct)
    {
        var connection = await connections.GetByIdAsync(t.ConnectionId, ct);
        var (active, last) = await RunsAsync(t.Id, ct);
        return new ConnectedTableSummaryResult(t.PublicId, destination.PublicId, destination.Name, destination.Icon,
            realms.GetValueOrDefault(t.SourceTenantId) ?? connection?.RealmSlug ?? "", connection?.SourceAppName ?? "", t.SourceTableName,
            t.IsCrossTenant, t.UpdateMode, ConnectedScheduling.Describe(ConnectedScheduling.Parse(t.ScheduleJson), t.TimeZone), ConnectedTime.Utc(t.NextRunOn),
            t.Status, t.AttentionReason, ConnectedTime.Utc(t.LastSuccessfulRunOn), active, last, await permissions.CanRefreshAsync(t, ct));
    }

    private async Task<(ConnectedRunResult? Active, ConnectedRunResult? Last)> RunsAsync(long connectedTableId, CancellationToken ct)
    {
        var recent = await runs.ListByTableAsync(connectedTableId, 5, ct);
        var names = await users.GetNamesByIdsAsync(recent.Select(r => r.TriggeredByUserId).Distinct(), ct);
        var active = recent.FirstOrDefault(r => !ConnectedRunStatuses.IsFinished(r.Status));
        var last = recent.FirstOrDefault(r => ConnectedRunStatuses.IsFinished(r.Status));
        return (active is null ? null : ConnectedRunHandlers.ToResult(active, names), last is null ? null : ConnectedRunHandlers.ToResult(last, names));
    }

    private async Task<ConnectedTableResult> BuildResultAsync(ConnectedTable t, CancellationToken ct)
    {
        var destination = await appTables.GetByIdAsync(t.AppTableId, ct);
        var app = await apps.GetByIdAsync(t.AppId, ct);
        var destinationFields = (await appFields.ListByTableAsync(destination.Id, ct) ?? Array.Empty<AppField>()).ToDictionary(f => f.Id);
        var mappings = await connectedTables.ListFieldsAsync(t.Id, ct);
        var fields = mappings
            .Where(m => destinationFields.ContainsKey(m.AppFieldId))
            .Select(m =>
            {
                var d = destinationFields[m.AppFieldId];
                return new ConnectedFieldResult(d.Fid, d.PublicId, d.Label ?? d.Name, d.TypeCode, m.SourceFid, m.SourceFieldName, m.SourceTypeCode,
                    m.Conversion, m.State, m.SourceFid == t.RefreshKeySourceFid);
            })
            .ToList();
        var keyLabel = t.RefreshKeySourceFid == ConnectedSystemFids.RecordId
            ? "Record ID# (in the source)"
            : mappings.FirstOrDefault(m => m.SourceFid == t.RefreshKeySourceFid)?.SourceFieldName ?? $"Field {t.RefreshKeySourceFid}";

        var isOwner = permissions.IsOwner(t);
        var isAdmin = await permissions.IsAppAdminAsync(t.AppId, ct);
        var canRefresh = isOwner || isAdmin || await permissions.CanRefreshAsync(t, ct);

        var connection = await connections.GetByIdAsync(t.ConnectionId, ct);
        ConnectionResult? connectionResult = null;
        if (connection is not null)
        {
            connectionResult = await connectionHandlers.ToResultAsync(connection, ct);
            // Credentials are the owner's business: other members see the connection, not its token prefix.
            if (!isOwner) connectionResult = connectionResult with { TokenPrefix = null, RowVersion = string.Empty };
        }
        var ownerName = (await users.GetNamesByIdsAsync([t.OwnerUserId], ct)).GetValueOrDefault(t.OwnerUserId) ?? "Unknown user";
        var realm = (await control.GetTenantAsync(t.SourceTenantId, ct))?.Name ?? connection?.RealmSlug ?? "";

        var refreshRoleIds = (await connectedTables.ListRefreshRoleIdsAsync(t.Id, ct)).ToHashSet();
        var roles = (await appRoles.ListDetailsByAppIdAsync(t.AppId, ct))
            .Select(r => new ConnectedRoleResult(r.PublicId, r.Name, refreshRoleIds.Contains(r.Id)))
            .ToList();
        var schedule = ConnectedScheduling.Parse(t.ScheduleJson);
        var (active, last) = await RunsAsync(t.Id, ct);

        return new ConnectedTableResult(t.PublicId, app.PublicId, destination.PublicId, destination.Name, destination.Icon,
            connectionResult, ownerName, realm, connection?.SourceAppName ?? "", t.SourceTablePublicId, t.SourceTableName, t.IsCrossTenant,
            fields, t.RefreshKeySourceFid, keyLabel, t.RefreshKeyIsAuto, t.UpdateMode, t.FilterEnabled, ConnectedSyncProcessor.ParseFilter(t.FilterJson),
            schedule, ConnectedScheduling.Describe(schedule, t.TimeZone), t.TimeZone, ConnectedTime.Utc(t.NextRunOn), t.NotifyOnFailure, t.MassDeleteGuardPct,
            t.Status, t.AttentionReason, ConnectedTime.Utc(t.LastSuccessfulRunOn), active, last, roles,
            new ConnectedTablePermissionsResult(isOwner, isAdmin, isOwner || context.IsSuperAdmin, isOwner || isAdmin, canRefresh),
            ConnectedTime.Utc(t.CreatedOn), ConnectedTokens.RowVersion(t.RowVersion));
    }

    private static ValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
