using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Forms.Queries.GetFormLayout;

public class GetFormLayoutQueryHandler
{
    private readonly IFormRepository _formRepo;

    public GetFormLayoutQueryHandler(IFormRepository formRepo) => _formRepo = formRepo;

    public async Task<FormLayoutDetail> HandleAsync(GetFormLayoutQuery query, CancellationToken ct = default)
    {
        var form = await _formRepo.GetByPublicIdAsync(query.FormPublicId, ct);
        var sections = await _formRepo.GetLayoutAsync(form.Id, ct);

        return new FormLayoutDetail
        {
            FormId = form.PublicId,
            Sections = sections.Select(s => new FormSectionDetail
            {
                DbId        = s.Id,
                Id          = s.PublicId,
                Name        = s.Name,
                ColumnCount = s.ColumnCount,
                IsCollapsed = s.IsCollapsed,
                DisplayOrder = s.DisplayOrder,
                Elements    = s.Elements.Select(e => new FormElementDetail
                {
                    DbId             = e.Id,
                    Id               = e.PublicId,
                    AppFieldId       = e.AppFieldId,
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
                    DisplayOrder     = e.DisplayOrder,
                }).ToList(),
            }).ToList(),
        };
    }
}
