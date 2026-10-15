using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Commands.AddFormSection;

public class AddRelationshipFormSectionCommandHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // The section's single column spans the whole 12-column grid, so the report is full width.
    private const int GridCols = 12;
    private const int ColumnSpan = GridCols;
    private const int ReportRowSpan = 4;

    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFormRepository _formRepo;
    private readonly IAppRepository _appRepo;
    private readonly IFieldReferenceIndexer _refIndexer;

    public AddRelationshipFormSectionCommandHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IFormRepository formRepo,
        IAppRepository appRepo,
        IFieldReferenceIndexer refIndexer)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _formRepo = formRepo;
        _appRepo = appRepo;
        _refIndexer = refIndexer;
    }

    /// <summary>Returns how many forms received the section.</summary>
    public async Task<int> HandleAsync(AddRelationshipFormSectionCommand command, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);

        var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
        var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
        var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);

        var reportLink = parentFields.FirstOrDefault(f => f.TypeCode == "ReportLink"
            && FormulaTypeMap.ParseReportLinkSettings(f.Settings)?.RelationshipId == rel.Id);
        if (reportLink?.Fid is not { } reportLinkFid) return 0; // nothing for the report to filter by
        var addRecord = parentFields.FirstOrDefault(f => f.TypeCode == RelationshipFieldFactory.FormulaUrlTypeCode
            && FormulaTypeMap.ParseFormulaUrlSettings(f.Settings)?.RelationshipId == rel.Id);

        var appPublicId = await _appRepo.GetPublicIdByIdAsync(child.AppId, ct);
        // The same shape as the form designer's ReportElementSettings, with "Filter using report link settings"
        // bound to the relationship's Report Link field.
        var reportContent = JsonSerializer.Serialize(new
        {
            reportId = command.ReportPublicId.ToString(),
            reportName = command.ReportName,
            reportAppPublicId = appPublicId.ToString(),
            reportTablePublicId = child.PublicId.ToString(),
            filterMode = "reportLink",
            reportLinkFid,
            heightMode = "auto",
            heightPx = (int?)null,
            showToolbar = true,
            showCreateButton = true,
            openInSubForm = true,
            subFormId = (string?)null,
            embeddedForFid = (int?)null,
        }, JsonOpts);

        var added = 0;
        var forms = await _formRepo.ListByTableAsync(parent.PublicId, ct);
        foreach (var form in forms.Where(f => f.AutoAddNewFields && !f.IsQuickPeekForm))
        {
            // Re-running for the same report must not stack a second section.
            var layout = await _formRepo.GetLayoutAsync(form.Id, ct);
            var alreadyThere = layout.SelectMany(s => s.Blocks).SelectMany(b => b.Elements).Any(e =>
                e.ElementType == "Report" && e.ElementContent != null
                && e.ElementContent.Contains(command.ReportPublicId.ToString(), StringComparison.OrdinalIgnoreCase));
            if (alreadyThere) continue;

            var elements = new List<FormElement>
            {
                NewElement("Report", appFieldId: null, reportContent, rowStart: 1, rowSpan: ReportRowSpan, order: 1, showOnAdd: true),
            };
            // "Add {Child}" sits right under the report, shown in every mode like any other field.
            if (addRecord?.Fid is { } addFid)
                elements.Add(NewElement("Field", addFid, null, rowStart: 1 + ReportRowSpan, rowSpan: 1, order: 2, showOnAdd: true));

            await _formRepo.AppendSectionAsync(form.Id, new FormSection
            {
                Name = $"{child.Name} details",
                GridCols = GridCols,
                Blocks =
                [
                    new FormSectionBlock { Width = 1, DisplayOrder = 1, ColStart = 1, ColSpan = ColumnSpan, Elements = elements },
                ],
            }, ct);
            await _refIndexer.ReindexFormAsync(form.PublicId, ct);
            added++;
        }
        return added;
    }

    private static FormElement NewElement(string type, int? appFieldId, string? content, int rowStart, int rowSpan, int order, bool showOnAdd) => new()
    {
        ElementType = type,
        AppFieldId = appFieldId,
        ElementContent = content,
        ShowOnAdd = showOnAdd,
        DisplayOrder = order,
        ColStart = 1,
        ColSpan = ColumnSpan,
        RowStart = rowStart,
        RowSpan = rowSpan,
    };
}
