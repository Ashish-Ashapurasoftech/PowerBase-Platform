using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Forms.Commands.SaveFormLayout;

public class SaveFormLayoutCommandHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public SaveFormLayoutCommandHandler(
        IFormRepository formRepo,
        IAppFieldRepository fieldRepo,
        IAppTableRepository tableRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _formRepo = formRepo;
        _fieldRepo = fieldRepo;
        _tableRepo = tableRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(SaveFormLayoutCommand command, CancellationToken ct = default)
    {
        var validator = new SaveFormLayoutCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var form = await _formRepo.GetByPublicIdAsync(command.FormPublicId, ct);

        var tableFields = await _fieldRepo.ListByTableAsync(form.AppTableId, ct);
        var validFieldIds = tableFields.Select(f => f.Id).ToHashSet();

        var fieldElementIds = command.Sections
            .SelectMany(s => s.Elements)
            .Where(e => e.ElementType == "Field" && e.AppFieldId.HasValue)
            .Select(e => e.AppFieldId!.Value)
            .ToList();

        var invalidIds = fieldElementIds.Except(validFieldIds).ToList();
        if (invalidIds.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Elements"] = [$"AppFieldId(s) {string.Join(", ", invalidIds)} do not belong to this table."]
            });

        var sections = command.Sections.Select(s => new FormSection
        {
            TenantId     = _queryContext.TenantId,
            FormId       = form.Id,
            Name         = s.Name,
            ColumnCount  = s.ColumnCount,
            ColumnWidths = s.ColumnWidths,
            IsCollapsed  = s.IsCollapsed,
            Elements     = s.Elements.Select(e => new FormElement
            {
                TenantId         = _queryContext.TenantId,
                AppFieldId       = e.ElementType == "Field" ? e.AppFieldId : null,
                ElementType      = e.ElementType,
                ElementContent   = e.ElementContent,
                LabelMode        = e.LabelMode,
                CustomLabel      = e.CustomLabel,
                ShowOnAdd        = e.ShowOnAdd,
                ShowOnEdit       = e.ShowOnEdit,
                ShowOnView       = e.ShowOnView,
                WidthMode        = e.WidthMode,
                WidthValue       = e.WidthValue,
                HelpTextOverride = e.HelpTextOverride,
                IsReadOnly       = e.IsReadOnly,
                IsRequired       = e.IsRequired,
                DisplayAs        = e.DisplayAs,
            }).ToList(),
        }).ToList();

        await _formRepo.SaveLayoutAsync(form.Id, _queryContext.TenantId, sections, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Form, form.Id.ToString(),
            ct: ct);
    }
}
