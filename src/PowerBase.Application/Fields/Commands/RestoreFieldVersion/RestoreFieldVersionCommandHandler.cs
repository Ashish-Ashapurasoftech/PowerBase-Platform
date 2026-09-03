using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.RestoreFieldVersion;

/// <summary>Re-applies a prior AppFieldVersion snapshot as the field's current settings. Never
/// touches the version being restored from (or any other historical version) — it only ever reads
/// it, then appends a brand-new version recording the restore (requirement: restoring an old
/// version is itself an auditable change; previous versions remain untouched). Runs the restored
/// snapshot through the exact same FieldSettingsGuard invariants a normal Update does, so a version
/// that would be invalid against the field's *current* context (e.g. duplicates now exist where the
/// restored snapshot had Unique off) is rejected rather than silently applied.</summary>
public class RestoreFieldVersionCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldVersionRepository _versionRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly FieldSettingsGuard _guard;
    private readonly FieldVersionService _versionService;
    private readonly ITenantUnitOfWork _uow;
    private readonly IMessagePublisher _messagePublisher;
    private readonly IQueryContext _queryContext;
    private readonly IAzureSearchService _searchService;

    public RestoreFieldVersionCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IFieldVersionRepository versionRepo,
        IRecordRepository recordRepo,
        IAuditRepository auditRepo,
        ISchemaEngineService schemaEngine,
        FieldSettingsGuard guard,
        FieldVersionService versionService,
        ITenantUnitOfWork uow,
        IMessagePublisher messagePublisher,
        IQueryContext queryContext,
        IAzureSearchService searchService)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _versionRepo = versionRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
        _schemaEngine = schemaEngine;
        _guard = guard;
        _versionService = versionService;
        _uow = uow;
        _messagePublisher = messagePublisher;
        _queryContext = queryContext;
        _searchService = searchService;
    }

    public async Task HandleAsync(RestoreFieldVersionCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.CommitMessage))
            throw new ValidationException(new Dictionary<string, string[]> { ["CommitMessage"] = ["A reason for this restore is required."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var existing = await _fieldRepo.GetByPublicIdAsync(command.FieldPublicId, ct)
                       ?? throw new NotFoundException("Field", command.FieldPublicId);

        if (existing.AppTableId != table.Id)
            throw new NotFoundException("Field", command.FieldPublicId);

        var currentVersion = await _versionRepo.GetCurrentVersionNumberAsync(existing.Id, ct);
        if (command.VersionToRestore == currentVersion)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["VersionToRestore"] = ["This is already the currently active version."]
            });
        }

        var targetRow = await _versionRepo.GetByFieldAndVersionAsync(existing.Id, command.VersionToRestore, ct)
            ?? throw new NotFoundException("FieldVersion", command.VersionToRestore);

        var target = FieldSnapshot.FromJson(targetRow.SnapshotJson);
        var before = FieldSnapshot.From(existing);

        // System fields' Label never changes (same rule UpdateFieldCommandHandler enforces) — fall
        // back to the field's current Label/Name if an old snapshot somehow lacks one.
        var label = existing.IsSystem ? (existing.Label ?? existing.Name) : (target.Label ?? existing.Label ?? existing.Name);

        if (await _fieldRepo.LabelExistsInTableAsync(table.Id, label, excludeFieldId: existing.Id, ct: ct))
            throw new DuplicateException("Field", "label", label);

        _guard.ValidateSettingsAndCapabilities(
            existing.TypeCode, target.Settings, target.Settings ?? existing.Settings,
            label, target.IsRequired, target.IsUnique, target.DefaultValue);
        await _guard.ValidateRequiredHasDefaultOrNoRestrictedRolesAsync(existing.Id, label, target.IsRequired, target.DefaultValue, ct);
        await _guard.ValidateUniqueTransitionAsync(table, existing, label, target.IsUnique, ct);
        await _guard.ValidateEncryptionTransitionAsync(table, existing, target.IsEncrypted, ct);

        bool wasSearchable = existing.IsSearchable;

        await _uow.BeginAsync(ct);
        try
        {
            var affected = await _fieldRepo.UpdateAsync(
                existing.PublicId, table.Id,
                label, target.Description,
                target.IsRequired, target.DefaultValue,
                target.IsSearchable, target.IsSortable,
                target.IsFilterable, target.IsReportable, target.IsAuditable,
                target.IsUnique, target.IsEncrypted, target.Settings, ct, _uow.Transaction);

            if (affected == 0)
                throw new NotFoundException("Field", command.FieldPublicId);

            // Recorded even if `target` happens to equal the field's current state (a version row
            // simply won't be created — see FieldVersionService — matching the same "no-op update
            // creates no version" rule an ordinary save follows).
            await _versionService.CreateVersionIfChangedAsync(
                existing.Id, before, target, command.CommitMessage,
                FieldVersionChangeType.Restore, command.VersionToRestore, _uow.Transaction!, ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        if (target.IsUnique != existing.IsUnique)
        {
            existing.IsUnique = target.IsUnique;
            await _schemaEngine.SetUniqueAsync(table, existing, target.IsUnique, ct);
        }

        if (target.IsRequired && !string.IsNullOrWhiteSpace(target.DefaultValue) && !existing.IsRequired)
        {
            existing.IsRequired = target.IsRequired;
            existing.DefaultValue = target.DefaultValue;
            await _recordRepo.BackfillDefaultAsync(table, existing, target.DefaultValue!, ct);
        }

        if (target.IsSearchable != wasSearchable && existing.Fid.HasValue)
        {
            var isNullify = !target.IsSearchable;
            var docs = await _recordRepo.GetFieldBackfillBatchAsync(_queryContext.TenantId, table.AppId, table.Id, existing.Fid.Value, isNullify, page: 1, pageSize: 500, ct);

            if (docs.Count > 0)
                await _searchService.BulkIndexRecordsAsync(docs, ct);

            var action = target.IsSearchable ? PowerBase.Application.Common.Models.IndexAction.BackfillField : PowerBase.Application.Common.Models.IndexAction.NullifyField;
            var msg = new PowerBase.Application.Common.Models.SearchIndexMessage
            {
                Action = action,
                TenantId = _queryContext.TenantId,
                AppId = table.AppId,
                TableId = table.Id,
                FieldId = existing.Fid.Value,
                Page = 1
            };
            _ = _messagePublisher.PublishAsync(msg, default);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, existing.PublicId.ToString(),
            $"Field '{label}' restored to version {command.VersionToRestore} In TableName : {table.Name}", appId: table.AppId, ct: ct);
    }
}
