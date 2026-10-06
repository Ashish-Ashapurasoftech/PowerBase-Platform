using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Application.Relationships.Commands.UpdateSummaryField;

/// <summary>Edits a Summary field on a relationship's parent table. Summaries are computed at read
/// time (no physical column), so saving new settings is all it takes — the next read reflects
/// them. The change is recorded as a new field version, like any other field-settings edit.</summary>
public class UpdateSummaryFieldCommandHandler
{
    private const string DefaultCommitMessage = "Summary field settings updated";

    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppRepository _appRepo;
    private readonly FormulaEngine _engine;
    private readonly FieldVersionService _versionService;
    private readonly ITenantUnitOfWork _uow;
    private readonly RelationshipQueriesHandler _queries;
    private readonly IAuditRepository _auditRepo;

    public UpdateSummaryFieldCommandHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppRepository appRepo,
        FormulaEngine engine,
        FieldVersionService versionService,
        ITenantUnitOfWork uow,
        RelationshipQueriesHandler queries,
        IAuditRepository auditRepo)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _appRepo = appRepo;
        _engine = engine;
        _versionService = versionService;
        _uow = uow;
        _queries = queries;
        _auditRepo = auditRepo;
    }

    public async Task<RelationshipDto> HandleAsync(UpdateSummaryFieldCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Label))
            throw new ValidationException(new Dictionary<string, string[]> { ["label"] = ["Summary field label is required."] });
        var label = command.Label.Trim();

        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        // Only a Summary field that belongs to this relationship can be edited through it.
        var field = await _fieldRepo.GetByPublicIdAsync(command.FieldPublicId, ct);
        if (field is null
            || field.AppTableId != rel.ParentTableId
            || field.TypeCode != nameof(Domain.Enums.FieldTypeCode.Summary)
            || FormulaTypeMap.ParseSummarySettings(field.Settings)?.RelationshipId != rel.Id)
            throw new NotFoundException("Summary field", command.FieldPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var childFields = await _fieldRepo.ListByTableAsync(rel.ChildTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(rel.ParentTableId, ct);
        var app = await _appRepo.GetByIdAsync(parent.AppId, ct);
        var lookupSources = await SummaryLookupSources.LoadAsync(childFields, _fieldRepo, ct);

        var settings = RelationshipFieldFactory.Serialize(SummarySettingsBuilder.Build(rel, command.Function, command.TargetFid,
            command.MatchingCriteria, command.CombinedText, childFields, parentFields, app, lookupSources));

        if (await _fieldRepo.LabelExistsInTableAsync(parent.Id, label, excludeFieldId: field.Id, ct: ct))
            throw new DuplicateException("Field", "label", label);

        await SummaryResultTypeGuard.EnsureResultTypeChangeIsSafeAsync(
            field, parent, settings, _engine, _relRepo, _fieldRepo, ct);

        var before = FieldSnapshot.From(field);
        var after = before with { Label = label, Settings = settings };
        var commitMessage = string.IsNullOrWhiteSpace(command.CommitMessage) ? DefaultCommitMessage : command.CommitMessage.Trim();

        // The field row and its new version land together or not at all (same as UpdateField).
        await _uow.BeginAsync(ct);
        try
        {
            var affected = await _fieldRepo.UpdateAsync(
                field.PublicId, parent.Id,
                label, field.Description,
                field.IsRequired, field.DefaultValue,
                field.IsSearchable, field.IsSortable,
                field.IsFilterable, field.IsReportable, field.IsAuditable,
                field.IsUnique, field.IsEncrypted, field.IsAutoFill, settings, ct, _uow.Transaction);

            if (affected == 0)
                throw new NotFoundException("Summary field", command.FieldPublicId);

            await _versionService.CreateVersionIfChangedAsync(
                field.Id, before, after, commitMessage,
                FieldVersionChangeType.Update, restoredFromVersion: null, _uow.Transaction!, ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, rel.Id.ToString(),
            $"Summary field '{label}' updated on {parent.Name}", appId: parent.AppId, ct: ct);

        return await _queries.GetAsync(rel.PublicId, ct);
    }
}
