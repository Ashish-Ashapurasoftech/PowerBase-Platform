using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Forms.Commands.CreateForm;

public class CreateFormCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IFormRepository _formRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public CreateFormCommandHandler(
        IAppTableRepository tableRepo,
        IFormRepository formRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _formRepo = formRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task<FormDetail> HandleAsync(CreateFormCommand command, CancellationToken ct = default)
    {
        var validator = new CreateFormCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        var form = new Form
        {
            TenantId         = _queryContext.TenantId,
            AppTableId       = table.Id,
            Name             = command.Name,
            IsDefault        = false,
            AutoAddNewFields = true,
            ShowBuiltInFields = false,
            SaveOptions      = "SaveKeepWorking,SaveNew,SaveNext,SaveView",
            DisplayOrder     = 0,
            CreatedBy        = _queryContext.UserId,
        };

        var (_, publicId) = await _formRepo.CreateAsync(form, ct);

        var created = await _formRepo.GetByPublicIdAsync(publicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.Form, created.Id.ToString(), $"Form added: {command.Name}",
            appId: table.AppId, ct: ct);

        return MapToDetail(created);
    }

    internal static FormDetail MapToDetail(Form f) => new()
    {
        Id               = f.PublicId,
        Name             = f.Name,
        IsDefault        = f.IsDefault,
        AutoAddNewFields = f.AutoAddNewFields,
        ShowBuiltInFields = f.ShowBuiltInFields,
        SaveOptions      = f.SaveOptions,
        DisplayOrder     = f.DisplayOrder,
        CreatedOn        = f.CreatedOn,
        RowVersion       = f.RowVersion,
    };
}
