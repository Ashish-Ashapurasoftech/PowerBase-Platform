using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Formula.Binding;

namespace PowerBase.Application.Formulas;

/// <summary>
/// An <see cref="ITableAliasSchema"/> built from every (non-deleted) table in one app, matching a
/// bracket token against each table's stored <c>Alias</c> and resolving to its PublicId (the same
/// tableId shape <see cref="CrossTableQueryContext"/> already expects from a literal GUID
/// argument) — so a resolved <c>[_DBID_FILE_TYPES]</c> token flows into <c>GetRecords</c> exactly
/// like a hand-typed table GUID would today. Built once per compile call; callers evaluating many
/// rows against the same table (batched formula projection, mass update) should build it once and
/// reuse it rather than re-querying per row.
/// </summary>
public sealed class AppTableAliasSchema : ITableAliasSchema
{
    private readonly Dictionary<string, string> _byAlias;

    private AppTableAliasSchema(Dictionary<string, string> byAlias) => _byAlias = byAlias;

    public static async Task<AppTableAliasSchema> BuildAsync(IAppTableRepository tableRepo, long appId, CancellationToken ct = default)
    {
        // ListAliasesByAppAsync (not the general-purpose ListByAppAsync) deliberately — that one
        // joins in every field of every table in the app, which this has no use for at all and
        // which gets expensive on tables with hundreds/thousands of fields, especially since this
        // rebuilds on every call (e.g. CustomDataRuleValidator does this on every record write).
        var tables = await tableRepo.ListAliasesByAppAsync(appId, ct);
        var byAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables ?? Array.Empty<AppTableAliasDto>())
        {
            if (!string.IsNullOrWhiteSpace(t.Alias))
                byAlias[t.Alias] = t.PublicId.ToString();
        }
        return new AppTableAliasSchema(byAlias);
    }

    public bool TryResolve(string alias, out string tableId) => _byAlias.TryGetValue(alias, out tableId!);
}
