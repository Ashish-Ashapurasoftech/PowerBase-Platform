using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Forms.Commands.UpdateFormSettings;

public class UpdateFormSettingsCommandHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public UpdateFormSettingsCommandHandler(
        IFormRepository formRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _formRepo = formRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateFormSettingsCommand command, CancellationToken ct = default)
    {
        var validator = new UpdateFormSettingsCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var form = await _formRepo.GetByPublicIdAsync(command.FormPublicId, ct);

        await _formRepo.UpdateSettingsAsync(
            command.FormPublicId,
            command.Name,
            command.AutoAddNewFields,
            command.ShowBuiltInFields,
            command.SaveOptions,
            command.RowVersion,
            ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Form, form.Id.ToString(),
            ct: ct);
    }
}
