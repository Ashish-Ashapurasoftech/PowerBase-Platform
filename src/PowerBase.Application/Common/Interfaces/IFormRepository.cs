using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IFormRepository
{
    Task<Form> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<long> GetAppIdByPublicIdAsync(Guid formPublicId, CancellationToken ct = default);
    Task<IReadOnlyList<Form>> ListByTableAsync(Guid tablePublicId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(Form form, CancellationToken ct = default);
    Task<int> UpdateSettingsAsync(Guid publicId, string name, bool autoAddNewFields, bool showBuiltInFields,
        string saveOptions, byte[] rowVersion, CancellationToken ct = default);
    Task<int> DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<FormSection>> GetLayoutAsync(long formId, CancellationToken ct = default);
    Task SaveLayoutAsync(long formId, IReadOnlyList<FormSection> sections, CancellationToken ct = default);
    Task AppendFieldToLastSectionAsync(long formId, int fieldFid, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> DuplicateAsync(Guid sourcePublicId, string newName, long userId, CancellationToken ct = default);
    Task SetDefaultAsync(Guid tablePublicId, Guid formPublicId, CancellationToken ct = default);
    Task<IReadOnlyList<(Guid? RolePublicId, Guid? EditFormPublicId, Guid? AddFormPublicId)>> GetRoleFormOverridesAsync(Guid tablePublicId, CancellationToken ct = default);
    Task UpdateRoleFormOverridesAsync(Guid tablePublicId, IEnumerable<(Guid? RolePublicId, Guid? EditFormPublicId, Guid? AddFormPublicId)> overrides, CancellationToken ct = default);
}
