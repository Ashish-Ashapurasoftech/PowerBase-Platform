namespace PowerBase.Application.Forms.Commands.SaveFormLayout;

public record SaveFormLayoutCommand(
    Guid FormPublicId,
    IReadOnlyList<FormSectionLayout> Sections,
    // ── Grid-snap canvas (Phase 8) — optional, default-valued so every
    // pre-Phase-8 positional call site (importer, tests) keeps compiling. ──
    IReadOnlyList<FormPageLayout>? Pages = null,
    string? PageNavMode = null,
    bool? AlwaysTabsOnView = null,
    string? ThemeJson = null);

public record FormPageLayout(
    Guid? PublicId,
    string Heading,
    int DisplayOrder);

public record FormSectionLayout(
    Guid? PublicId,
    string Name,
    bool IsCollapsed,
    IReadOnlyList<FormBlockLayout> Blocks,
    int? GridCols = null,
    Guid? PagePublicId = null,
    bool? IsPinned = null,
    string? BackgroundColor = null,
    string? BackgroundType = null,
    string? BackgroundImage = null,
    string? BorderColor = null,
    int? BorderWidth = null,
    bool? ShowDividers = null,
    string? DividerColor = null,
    int? DividerWidthPx = null);

public record FormBlockLayout(
    Guid? PublicId,
    string? Heading,
    string? BackgroundColor,
    int? Width,
    IReadOnlyList<FormElementLayout> Elements,
    int? ColStart = null,
    int? ColSpan = null,
    string? BackgroundType = null,
    string? BackgroundImage = null,
    string? DividerMode = null,
    string? DividerColor = null,
    int? DividerWidthPx = null);

public record FormElementLayout(
    Guid? PublicId,
    long? AppFieldId,
    string ElementType,
    string? ElementContent,
    string LabelMode,
    string? CustomLabel,
    bool ShowOnAdd,
    bool ShowOnEdit,
    bool ShowOnView,
    string WidthMode,
    int? WidthValue,
    string? HelpTextOverride,
    bool IsReadOnly,
    bool IsRequired,
    string? DisplayAs,
    int? ColStart = null,
    int? RowStart = null,
    int? ColSpan = null,
    int? RowSpan = null,
    Guid? GroupId = null,
    Guid? CloneGroupId = null,
    Guid? PagePublicId = null,
    string? TextStyle = null,
    string? BackgroundColor = null,
    string? BorderColor = null,
    int? BorderWidth = null,
    string? ContentWidthMode = null,
    int? ContentWidthValue = null,
    string? ContentWidthUnit = null);
