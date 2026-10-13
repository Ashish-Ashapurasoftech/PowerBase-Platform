using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Formulas;

/// <summary>Loads the app variables a formula may reference. A caller that wasn't given a variable repository
/// (a test, or a code path that doesn't support variables) gets none, so every name resolves as a field as before.</summary>
public static class FormulaVariables
{
    public static async Task<IReadOnlyList<AppVariable>> LoadAsync(IAppVariableRepository? repo, long appId, CancellationToken ct = default)
        => repo is null ? [] : await repo.ListAsync(appId, ct);
}
