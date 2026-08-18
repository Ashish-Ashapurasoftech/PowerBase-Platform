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
        var pages = await _formRepo.GetPagesAsync(form.Id, ct);

        var pagePublicIdById = pages.ToDictionary(p => p.Id, p => p.PublicId);
        Guid? ResolvePageId(long? formPageId) =>
            formPageId.HasValue && pagePublicIdById.TryGetValue(formPageId.Value, out var publicId) ? publicId : null;

        return new FormLayoutDetail
        {
            FormId           = form.PublicId,
            PageNavMode      = form.PageNavMode ?? "tabs",
            AlwaysTabsOnView = form.AlwaysTabsOnView ?? true,
            ThemeJson        = form.ThemeJson,
            Pages = pages.Select(p => new FormPageDetail
            {
                DbId         = p.Id,
                Id           = p.PublicId,
                Heading      = p.Heading,
                DisplayOrder = p.DisplayOrder,
            }).ToList(),
            Sections = sections.Select(s => new FormSectionDetail
            {
                DbId         = s.Id,
                Id           = s.PublicId,
                Name         = s.Name,
                ColumnCount  = s.ColumnCount,
                ColumnWidths = s.ColumnWidths,
                IsCollapsed  = s.IsCollapsed,
                DisplayOrder = s.DisplayOrder,
                GridCols        = s.GridCols,
                PageId          = ResolvePageId(s.FormPageId),
                IsPinned        = s.IsPinned,
                BackgroundColor = s.BackgroundColor,
                BackgroundType  = s.BackgroundType,
                BackgroundImage = s.BackgroundImage,
                BorderColor     = s.BorderColor,
                BorderWidth     = s.BorderWidth,
                ShowDividers    = s.ShowDividers,
                DividerColor    = s.DividerColor,
                DividerWidthPx  = s.DividerWidthPx,
                Blocks       = s.Blocks.Select(b => new FormBlockDetail
                {
                    DbId            = b.Id,
                    Id              = b.PublicId,
                    Heading         = b.Heading,
                    BackgroundColor = b.BackgroundColor,
                    Width           = b.Width,
                    DisplayOrder    = b.DisplayOrder,
                    ColStart        = b.ColStart,
                    ColSpan         = b.ColSpan,
                    BackgroundType  = b.BackgroundType,
                    BackgroundImage = b.BackgroundImage,
                    DividerMode     = b.DividerMode,
                    DividerColor    = b.DividerColor,
                    DividerWidthPx  = b.DividerWidthPx,
                    Elements        = b.Elements.Select(e => new FormElementDetail
                    {
                        DbId             = e.Id,
                        Id               = e.PublicId,
                        AppFieldId       = e.AppFieldId,
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
                        DisplayOrder     = e.DisplayOrder,
                        ColStart          = e.ColStart,
                        RowStart          = e.RowStart,
                        ColSpan           = e.ColSpan,
                        RowSpan           = e.RowSpan,
                        GroupId           = e.GroupId,
                        CloneGroupId      = e.CloneGroupId,
                        PageId            = ResolvePageId(e.FormPageId),
                        TextStyle         = e.TextStyle,
                        BackgroundColor   = e.BackgroundColor,
                        BorderColor       = e.BorderColor,
                        BorderWidth       = e.BorderWidth,
                        ContentWidthMode  = e.ContentWidthMode,
                        ContentWidthValue = e.ContentWidthValue,
                        ContentWidthUnit  = e.ContentWidthUnit,
                    }).ToList(),
                }).ToList(),
            }).ToList(),
        };
    }
}
