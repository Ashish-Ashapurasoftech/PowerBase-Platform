using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships.Commands.AddSummaryField;

/// <summary>Creates a Summary field on an existing relationship's parent table that rolls up the
/// related child records (count / true-false / aggregate of a field), optionally filtered.</summary>
public class AddSummaryFieldCommandHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
        // Stored in canonical casing ("sum" → "Sum"): the aggregation code matches names exactly.
        var function = SummaryFunctions.Normalize(command.Function)
            ?? throw new ValidationException(new Dictionary<string, string[]> { ["function"] = [$"Unknown summary function '{command.Function}'."] });

        var needsTarget = function is not (SummaryFunctions.Count or SummaryFunctions.Exists);

        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        var childFields = await _fieldRepo.ListByTableAsync(child.Id, ct);

        var target = command.TargetFid.HasValue ? childFields.FirstOrDefault(f => f.Fid == command.TargetFid) : null;
        SummaryTargetValidator.Validate(function, command.TargetFid, target, targetSubField: null);
        var combinedText = SummaryTargetValidator.ValidateCombinedTextOptions(function, command.CombinedText, childFields);

        // Encrypted columns can't be summarized yet — refuse rather than create a summary that
        // would compute nothing (or expose ciphertext).
        var app = await _appRepo.GetByIdAsync(parent.AppId, ct);
        if (SummaryEncryptionGuard.FindProblem(app, childFields, rel.ReferenceFid,
                needsTarget ? command.TargetFid : null, combinedText?.SortFid, command.MatchingCriteria) is { } encryptionProblem)
            throw new ValidationException(new Dictionary<string, string[]> { ["targetFid"] = [encryptionProblem] });

        var filterJson = command.MatchingCriteria is { Nodes.Count: > 0 }
            ? JsonSerializer.Serialize(command.MatchingCriteria, JsonOpts)
            : null;

        var summary = await _fieldFactory.CreateAsync(parent, nameof(Domain.Enums.FieldTypeCode.Summary),
            command.Label.Trim(), false,
            new SummarySettings
            {
                RelationshipId = rel.Id,
                ChildTableId = child.Id,
                ReferenceFid = rel.ReferenceFid,
                Function = function,
                TargetFid = needsTarget ? command.TargetFid : null,
                TargetTypeCode = target?.TypeCode,
                FilterTree = filterJson,
                Delimiter = combinedText?.Delimiter,
                SortFid = combinedText?.SortFid,
                SortDescending = combinedText?.SortDescending ?? false,
                DistinctValues = combinedText?.DistinctValues ?? false,
            }, ct);

        await _fieldFactory.AppendToAutoAddFormsAsync(parent.PublicId, new[] { summary.Fid!.Value }, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.AppField, rel.Id.ToString(),
            $"Summary field '{summary.Name}' added to {parent.Name}", appId: parent.AppId, ct: ct);

        return await _queries.GetAsync(rel.PublicId, ct);
    }
}
