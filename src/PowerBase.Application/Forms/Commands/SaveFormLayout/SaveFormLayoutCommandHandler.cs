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
        var validFieldIds = tableFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();

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

        // PublicId is carried through so a caller can keep a saved section/block/element's
        // identity across this save — the layout is re-inserted wholesale, so without it every
        // row would come back with a new id. Absent (null) means "generate one", which is what
        // newly-added rows send. Guid.Empty is the entity-level stand-in for that absence.
        var sections = command.Sections.Select(s => new FormSection
        {
            PublicId    = s.PublicId ?? Guid.Empty,
            FormId      = form.Id,
            Name        = s.Name,
            ColumnCount = s.Blocks.Count,
            IsCollapsed = s.IsCollapsed,
            GridCols        = s.GridCols ?? 12,
            // PagePublicId is resolved to a FormPageId by the repository, which knows the
            // freshly-(re)inserted page ids — this layer only carries the public identity through.
            BackgroundColor = s.BackgroundColor,
            BackgroundType  = s.BackgroundType,
            BackgroundImage = s.BackgroundImage,
            BorderColor     = s.BorderColor,
            BorderWidth     = s.BorderWidth,
            ShowDividers    = s.ShowDividers ?? true,
            DividerColor    = s.DividerColor,
            DividerWidthPx  = s.DividerWidthPx,
            IsPinned        = s.IsPinned ?? false,
            Blocks      = s.Blocks.Select(b => new FormSectionBlock
            {
                PublicId        = b.PublicId ?? Guid.Empty,
                Heading         = b.Heading,
                BackgroundColor = b.BackgroundColor,
                Width           = b.Width,
                ColStart        = b.ColStart,
                ColSpan         = b.ColSpan,
                BackgroundType  = b.BackgroundType,
                BackgroundImage = b.BackgroundImage,
                DividerMode     = b.DividerMode,
                DividerColor    = b.DividerColor,
                DividerWidthPx  = b.DividerWidthPx,
                Elements        = b.Elements.Select(e => new FormElement
                {
                    PublicId         = e.PublicId ?? Guid.Empty,
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
                    ColStart          = e.ColStart,
                    RowStart          = e.RowStart,
                    ColSpan           = e.ColSpan,
                    RowSpan           = e.RowSpan,
                    GroupId           = e.GroupId,
                    CloneGroupId      = e.CloneGroupId,
                    TextStyle         = e.TextStyle,
                    BackgroundColor   = e.BackgroundColor,
                    BorderColor       = e.BorderColor,
                    BorderWidth       = e.BorderWidth,
                    ContentWidthMode  = e.ContentWidthMode,
                    ContentWidthValue = e.ContentWidthValue,
                    ContentWidthUnit  = e.ContentWidthUnit,
                }).ToList(),
            }).ToList(),
        }).ToList();

        // Section/element -> page linkage is carried as a PublicId (FormSectionLayout.PagePublicId /
        // FormElementLayout.PagePublicId) alongside the page list itself, since the FormPage rows
        // are being re-inserted in this same save and don't have stable internal ids yet either.
        var pages = command.Pages?.Select(p => new FormPage
        {
            PublicId     = p.PublicId ?? Guid.Empty,
            FormId       = form.Id,
            Heading      = p.Heading,
            DisplayOrder = p.DisplayOrder,
        }).ToList();

        var sectionPageLinks = command.Sections
            .Select((s, i) => (Section: sections[i], PagePublicId: s.PagePublicId))
            .Where(x => x.PagePublicId.HasValue)
            .ToDictionary(x => x.Section, x => x.PagePublicId!.Value);

        var elementPageLinks = command.Sections
            .SelectMany(s => s.Blocks)
            .SelectMany(b => b.Elements)
            .Zip(sections.SelectMany(s => s.Blocks).SelectMany(b => b.Elements))
            .Where(x => x.First.PagePublicId.HasValue)
            .ToDictionary(x => x.Second, x => x.First.PagePublicId!.Value);

        await _formRepo.SaveLayoutAsync(form.Id, sections, pages, command.PageNavMode,
            command.AlwaysTabsOnView, command.ThemeJson, sectionPageLinks, elementPageLinks, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Form, form.Id.ToString(),
            ct: ct);
    }
}
