using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms.Commands.CreateForm;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Forms.Commands.DuplicateForm;

public class DuplicateFormCommandHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public DuplicateFormCommandHandler(
        IFormRepository formRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _formRepo = formRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task<FormDetail> HandleAsync(DuplicateFormCommand command, CancellationToken ct = default)
    {
        var source = await _formRepo.GetByPublicIdAsync(command.FormPublicId, ct);
        var newName = command.Name ?? $"{source.Name} (copy)";

        var (_, newPublicId) = await _formRepo.DuplicateAsync(
            command.FormPublicId, newName, _queryContext.TenantId, _queryContext.UserId, ct);

        var created = await _formRepo.GetByPublicIdAsync(newPublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.Form, created.Id.ToString(),
            ct: ct);

        return CreateFormCommandHandler.MapToDetail(created);
    }
}
