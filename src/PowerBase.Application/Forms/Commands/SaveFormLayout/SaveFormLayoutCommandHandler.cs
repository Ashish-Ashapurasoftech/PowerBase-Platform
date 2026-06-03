using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Forms.Commands.SaveFormLayout;

public class SaveFormLayoutCommandHandler
{
    private readonly IFormRepository _formRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public SaveFormLayoutCommandHandler(
        IFormRepository formRepo,
        IAppFieldRepository fieldRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _formRepo = formRepo;
        _fieldRepo = fieldRepo;
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

        // Collect all field element IDs across all blocks
        var fieldElementIds = command.Sections
            .SelectMany(s => s.Blocks)
            .SelectMany(b => b.Elements)
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
            FormId      = form.Id,
            Name        = s.Name,
            ColumnCount = s.Blocks.Count,
            IsCollapsed = s.IsCollapsed,
            Blocks      = s.Blocks.Select(b => new FormSectionBlock
            {
                Heading         = b.Heading,
                BackgroundColor = b.BackgroundColor,
                Width           = b.Width,
                Elements        = b.Elements.Select(e => new FormElement
                {
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
            }).ToList(),
        }).ToList();

        await _formRepo.SaveLayoutAsync(form.Id, sections, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Form, form.Id.ToString(),
            ct: ct);
    }
}
