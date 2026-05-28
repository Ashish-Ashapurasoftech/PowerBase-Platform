namespace PowerBase.Application.Forms.Commands.SaveFormLayout;

public record SaveFormLayoutCommand(
    Guid FormPublicId,
    IReadOnlyList<FormSectionLayout> Sections);

public record FormSectionLayout(
    Guid? PublicId,
    string Name,
    bool IsCollapsed,
    IReadOnlyList<FormBlockLayout> Blocks);

public record FormBlockLayout(
    Guid? PublicId,
    string? Heading,
    string? BackgroundColor,
    int? Width,
    IReadOnlyList<FormElementLayout> Elements);

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
    string? DisplayAs);
