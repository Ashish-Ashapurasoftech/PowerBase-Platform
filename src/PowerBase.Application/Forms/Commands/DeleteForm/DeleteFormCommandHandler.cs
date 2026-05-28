using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Forms.Commands.DeleteForm;

public class DeleteFormCommandHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public DeleteFormCommandHandler(
        IFormRepository formRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _formRepo = formRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteFormCommand command, CancellationToken ct = default)
    {
        var form = await _formRepo.GetByPublicIdAsync(command.FormPublicId, ct);

        if (form.IsDefault)
            throw new BadRequestException("FORM_DEFAULT_DELETE", "The default form cannot be deleted.");

        await _formRepo.DeleteAsync(command.FormPublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.Form, form.Id.ToString(),
            ct: ct);
    }
}
