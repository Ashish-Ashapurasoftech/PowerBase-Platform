using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Formula;

namespace PowerBase.Application.Records.Commands.CreateRecord;

public class CreateRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAuditRepository _auditRepo;
    //private readonly IFormulaDefaultResolver _formulaDefaults;
    private readonly IPipelineTriggerInterceptor _triggerInterceptor;
    private readonly ITenantUnitOfWork _uow;
    private readonly IQueryContext _queryContext;
    private readonly IMessagePublisher _messagePublisher;
    private readonly IReferenceRelinkService? _relink;
    private readonly RecordCreatePreparer _preparer;

    public CreateRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRelationshipRepository relRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo,
        //IFormulaDefaultResolver formulaDefaults,
        IPipelineTriggerInterceptor triggerInterceptor,
        ITenantUnitOfWork uow,
        IQueryContext queryContext,
        IUserRepository userRepo,
        IMessagePublisher messagePublisher,
        FormulaEngine engine,
        IAppRepository appRepo,
        IFormRuleRepository formRuleRepo,
        IFormRepository formRepo,
        IAppUserRepository appUserRepo,
        IReferenceRelinkService? relink = null)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _auditRepo = auditRepo;
        //_formulaDefaults = formulaDefaults;
        _triggerInterceptor = triggerInterceptor;
        _uow = uow;
        _queryContext = queryContext;
        _messagePublisher = messagePublisher;
        _relink = relink;
        _preparer = new RecordCreatePreparer(
            tableRepo, fieldRepo, recordRepo, relRepo, enforcer, queryContext, userRepo, appRepo, engine, formRuleRepo, formRepo, appUserRepo);
    }

    public async Task<RecordResult> HandleAsync(CreateRecordCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        // Field ids, permissions, Reference resolution, defaults, User ids, then Required / Unique /
        // format, the Custom Data Rule and Form Rules — the same preparation Grid Edit's bulk create runs.
        await _preparer.EnsureWritableAsync(table, fields, command.FieldValues.Keys.ToList(), ct);
        var effectiveValues = await _preparer.BuildEffectiveValuesAsync(table, fields, command.FieldValues, ct);
        var dateFormat = await _preparer.GetAppDateFormatAsync(table, ct);
        await _preparer.ValidateAsync(table, fields, effectiveValues, dateFormat, ct);

        Guid publicId;
        PowerBase.Application.Common.Models.SearchIndexMessage? indexMessage = null;

        await _uow.BeginAsync(ct);
        try
        {
            publicId = await _recordRepo.CreateAsync(table, fields, effectiveValues, _uow.Transaction, ct, msg => indexMessage = msg);

            var recordId = await _recordRepo.GetRecordIdByPublicIdAsync(table, publicId, _uow.Transaction, ct);
            effectiveValues[3] = recordId;

            // Child records whose reference found no parent until now (their text kept in the hidden "(original)" field) and
            // whose text is this record's key are linked to it by a background job, queued here to run once this save commits.
            if (_relink is not null)
                await _relink.QueueAddedAsync(table, [new RelinkParent(recordId, effectiveValues)], ct);

            // Date Created / Record Owner are system-managed — never present in effectiveValues
            // otherwise, since nothing submits them — so a pipeline trigger firing off this create
            // (via InterceptAsync below) could never resolve {{steps.<trigger>.fid_N}} for them.
            // Looked up by PhysicalColumnName rather than a hardcoded fid, same as
            // PhysicalNaming.GetPhysicalColumnName's system-field convention used elsewhere.
            var createdOnField = fields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedOn" && f.Fid.HasValue);
            if (createdOnField != null) effectiveValues[createdOnField.Fid!.Value] = DateTime.UtcNow;
            var createdByField = fields.FirstOrDefault(f => f.IsSystem && f.PhysicalColumnName == "CreatedBy" && f.Fid.HasValue);
            if (createdByField != null) effectiveValues[createdByField.Fid!.Value] = _queryContext.UserId;

            await _auditRepo.LogActivityAsync(
                AuditActions.Created, AuditEntityTypes.Record, publicId.ToString(), $"Record added in {table.Name} with ID {publicId}", appId: table.AppId, ct: ct);

            await _tableRepo.IncrementRecordCountAsync(table.Id, ct);

            await _triggerInterceptor.InterceptAsync(table, fields, publicId, effectiveValues, "record-added", ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (indexMessage != null)
        {
            _ = _messagePublisher.PublishPriorityAsync(indexMessage, default).ContinueWith(
                t => { /* Observed fire-and-forget: failure logged if faulted */ },
                TaskContinuationOptions.OnlyOnFaulted);
        }

        var fieldData = new Dictionary<string, object?>();
        foreach (var field in fields.Where(f => f.Fid.HasValue && effectiveValues.ContainsKey((long)f.Fid.Value)))
            fieldData[field.Fid!.Value.ToString()] = effectiveValues[(long)field.Fid.Value];

        return new RecordResult
        {
            Id = publicId,
            CreatedOn = DateTime.UtcNow,
            ModifiedOn = null,
            Fields = fieldData,
        };
    }
}
