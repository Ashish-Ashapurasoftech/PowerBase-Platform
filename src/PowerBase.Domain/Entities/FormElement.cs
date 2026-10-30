namespace PowerBase.Domain.Entities;

public class FormElement
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long FormSectionId { get; set; }
    public long? FormSectionBlockId { get; set; }
    public long? AppFieldId { get; set; }
    public string ElementType { get; set; } = "Field";
    public string? ElementContent { get; set; }
    public string LabelMode { get; set; } = "Default";
    public string? CustomLabel { get; set; }
    /// <summary>Internal component name for non-field elements ("Text 1", "Divider 1"). Null for fields,
    /// which are known by their field label. Lets form rules reference every component reliably.</summary>
    public string? Name { get; set; }
    public bool ShowOnAdd { get; set; } = true;
    public bool ShowOnEdit { get; set; } = true;
    public bool ShowOnView { get; set; } = true;
    public string WidthMode { get; set; } = "Auto";
    public int? WidthValue { get; set; }
    public string? HelpTextOverride { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsRequired { get; set; }
    public string? DisplayAs { get; set; }
    public int DisplayOrder { get; set; }

    // ── Grid-snap canvas (Phase 8) — all nullable; null means "not yet
    // placed on the grid" (a legacy row, derived client-side on first open). ──
    public int? ColStart { get; set; }
    public int? RowStart { get; set; }
    public int? ColSpan { get; set; }
    public int? RowSpan { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? CloneGroupId { get; set; }
    public long? FormPageId { get; set; }
    public string? TextStyle { get; set; }
    public string? BackgroundColor { get; set; }
    public string? BorderColor { get; set; }
    public int? BorderWidth { get; set; }
    public string? ContentWidthMode { get; set; }
    public int? ContentWidthValue { get; set; }
    public string? ContentWidthUnit { get; set; }

    // ── Reference / reference-proxy elements: which parent fields the dropdown shows ──
    /// <summary>"default" | "report" | "field"; null = "default" (the parent table's default record picker).</summary>
    public string? RecordPickerMode { get; set; }
    /// <summary>Mode "report": PublicId of a Table report on the parent table.</summary>
    public Guid? RecordPickerReportPublicId { get; set; }
    /// <summary>Mode "field": Fid (like <see cref="AppFieldId"/>) of a Lookup field of this relationship on the form's table.</summary>
    public long? RecordPickerFieldId { get; set; }
}
