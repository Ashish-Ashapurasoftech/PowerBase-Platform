using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppCopyRepository
{
    /// <summary>Copies an app's structure (no records) with meta.usp_CopyAppSchema, in one transaction together with the
    /// new app's data key when the app is encrypted. Throws <c>DuplicateException</c> when the name is taken.</summary>
    Task<AppSchemaCopyResult> CopySchemaAsync(AppSchemaCopyRequest request, CancellationToken ct = default);
}
