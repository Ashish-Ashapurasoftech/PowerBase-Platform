using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IFormRepository
{
    Task<Form> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<long> GetAppIdByPublicIdAsync(Guid formPublicId, CancellationToken ct = default);
    /// <summary>The internal AppTable id the form belongs to, or null if the form is missing.</summary>
    Task<long?> GetTableIdByFormIdAsync(long formId, CancellationToken ct = default);
    Task<IReadOnlyList<Form>> ListByTableAsync(Guid tablePublicId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(Form form, CancellationToken ct = default);
    Task<int> UpdateSettingsAsync(Guid publicId, string name, bool autoAddNewFields, bool showBuiltInFields,
        string saveOptions, byte[] rowVersion, CancellationToken ct = default);
    Task<int> DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<FormSection>> GetLayoutAsync(long formId, CancellationToken ct = default);
    Task<IReadOnlyList<FormPage>> GetPagesAsync(long formId, CancellationToken ct = default);

    /// <summary>Replaces a form's entire layout (pages + sections + blocks + elements) in one
    /// transaction, and updates the form-level page-nav/theme settings alongside it — pages are
    /// part of the layout, not a separate endpoint. <paramref name="sectionPageLinks"/> and
    /// <paramref name="elementPageLinks"/> map a section/element (by reference, matching the
    /// instances inside <paramref name="sections"/>) to the PublicId of the page it belongs to,
    /// resolved against the freshly-(re)inserted <paramref name="pages"/> in the same transaction.</summary>
    Task SaveLayoutAsync(
        long formId,
        IReadOnlyList<FormSection> sections,
        IReadOnlyList<FormPage>? pages = null,
        string? pageNavMode = null,
        bool? alwaysTabsOnView = null,
        string? themeJson = null,
        IReadOnlyDictionary<FormSection, Guid>? sectionPageLinks = null,
        IReadOnlyDictionary<FormElement, Guid>? elementPageLinks = null,
        CancellationToken ct = default);
    Task AppendFieldToLastSectionAsync(long formId, int fieldFid, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> DuplicateAsync(Guid sourcePublicId, string newName, long userId, CancellationToken ct = default);
    Task SetDefaultAsync(Guid tablePublicId, Guid formPublicId, CancellationToken ct = default);
    Task<IReadOnlyList<(Guid? RolePublicId, Guid? EditFormPublicId, Guid? AddFormPublicId)>> GetRoleFormOverridesAsync(Guid tablePublicId, CancellationToken ct = default);
    Task UpdateRoleFormOverridesAsync(Guid tablePublicId, IEnumerable<(Guid? RolePublicId, Guid? EditFormPublicId, Guid? AddFormPublicId)> overrides, CancellationToken ct = default);
}
