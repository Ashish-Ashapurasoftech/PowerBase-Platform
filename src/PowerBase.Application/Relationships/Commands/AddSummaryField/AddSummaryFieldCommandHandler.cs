using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Commands.AddSummaryField;

/// <summary>Creates a Summary field on an existing relationship's parent table that rolls up the
/// related child records (count / true-false / aggregate of a field), optionally filtered.</summary>
public class AddSummaryFieldCommandHandler
{
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly RelationshipFieldFactory _fieldFactory;
    private readonly RelationshipQueriesHandler _queries;
    private readonly IAuditRepository _auditRepo;
    private readonly IAppRepository _appRepo;

    public AddSummaryFieldCommandHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        RelationshipFieldFactory fieldFactory,
        RelationshipQueriesHandler queries,
        IAuditRepository auditRepo,
        IAppRepository appRepo)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _fieldFactory = fieldFactory;
        _queries = queries;
        _auditRepo = auditRepo;
        _appRepo = appRepo;
    }

    public async Task<RelationshipDto> HandleAsync(AddSummaryFieldCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Label))
            throw new ValidationException(new Dictionary<string, string[]> { ["label"] = ["Summary field label is required."] });
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var childFields = await _fieldRepo.ListByTableAsync(rel.ChildTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(rel.ParentTableId, ct);
        var app = await _appRepo.GetByIdAsync(parent.AppId, ct);
        var lookupSources = await SummaryLookupSources.LoadAsync(childFields, _fieldRepo, ct);

        var settings = SummarySettingsBuilder.Build(rel, command.Function, command.TargetFid,
            command.MatchingCriteria, command.CombinedText, childFields, parentFields, app, lookupSources);

        var summary = await _fieldFactory.CreateAsync(parent, nameof(Domain.Enums.FieldTypeCode.Summary),
            command.Label.Trim(), false, settings, ct);

        await _fieldFactory.AppendToAutoAddFormsAsync(parent.PublicId, new[] { summary.Fid!.Value }, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, rel.Id.ToString(),
            $"Summary field '{summary.Name}' added to {parent.Name}", appId: parent.AppId, ct: ct);

        return await _queries.GetAsync(rel.PublicId, ct);
    }
}
