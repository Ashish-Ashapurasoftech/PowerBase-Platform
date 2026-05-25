using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.CreateRecord;

public class CreateRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;

    public CreateRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
    }

    public async Task<RecordResult> HandleAsync(CreateRecordCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var tableFieldIds = new HashSet<long>(fields.Select(f => f.Id));
        var unknownIds = command.FieldValues.Keys.Where(k => !tableFieldIds.Contains(k)).ToList();
        if (unknownIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"Unknown field IDs: {string.Join(", ", unknownIds)}"] });

        var publicId = await _recordRepo.CreateAsync(table, fields, command.FieldValues, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.Record, publicId.ToString(), appId: table.AppId, ct: ct);

        await _tableRepo.IncrementRecordCountAsync(table.Id, ct);

        var fieldData = new Dictionary<string, object?>();
        foreach (var field in fields.Where(f => command.FieldValues.ContainsKey(f.Id)))
            fieldData[field.Id.ToString()] = command.FieldValues[field.Id];

        return new RecordResult
        {
            Id = publicId,
            CreatedOn = DateTime.UtcNow,
            ModifiedOn = null,
            Fields = fieldData,
        };
    }
}
