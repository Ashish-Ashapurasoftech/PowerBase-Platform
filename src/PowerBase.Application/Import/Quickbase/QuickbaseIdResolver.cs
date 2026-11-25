using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Import.Quickbase;

/// <summary>One table of the file and the Quickbase table id matched to it.</summary>
public sealed class QuickbaseTableIds
{
    /// <summary>The table's logical id in the file (e.g. <c>$Table_Clients</c>) — stored as <c>QBTableLogicalID</c>.</summary>
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>The file's <c>AliasHint</c> (e.g. <c>_DBID_CLIENTS</c>), matched against the API's <c>alias</c>.</summary>
    public string? AliasHint { get; set; }
    /// <summary>The Quickbase table id (e.g. <c>bunx343yd</c>) — stored as <c>QBTableID</c>. Null when no Quickbase table matched.</summary>
    public string? QbTableId { get; set; }
}

/// <summary>The Quickbase ids of one application of the file, as the import wizard shows them.</summary>
public sealed class QuickbaseAppIds
{
    public string? QbAppId { get; set; }
    /// <summary>The app's logical id in the file (e.g. <c>$App_HIH_Archive</c>) — stored as <c>QBAppLogicalID</c>.</summary>
    public string QbAppLogicalId { get; set; } = string.Empty;
    /// <summary>Catalog (from meta.QuickbaseAppCatalog), Manual (typed by the user) or Missing.</summary>
    public string AppIdSource { get; set; } = "Missing";
    /// <summary>Result of reading the app's tables from Quickbase: Ok, NoAccess, NotFound, Failed or NotTried.</summary>
    public string TokenStatus { get; set; } = nameof(QuickbaseLookupStatus.NotTried);
    public string? Message { get; set; }
    public List<QuickbaseTableIds> Tables { get; set; } = [];

    /// <summary>The wizard must ask for an app id.</summary>
    public bool NeedsAppId => string.IsNullOrWhiteSpace(QbAppId);
    /// <summary>The wizard must ask for a user token or a retry (the app id is known, but its tables could not be read: no token
    /// has access, Quickbase did not find it, or the call failed - the message then says why).</summary>
    public bool NeedsUserToken => !NeedsAppId && TokenStatus is nameof(QuickbaseLookupStatus.NoAccess) or nameof(QuickbaseLookupStatus.NotFound)
        or nameof(QuickbaseLookupStatus.Failed);
    public int MatchedTableCount => Tables.Count(t => !string.IsNullOrEmpty(t.QbTableId));
}

/// <summary>A table of the file, as the resolver needs it.</summary>
public sealed record QuickbaseFileTable(string LogicalRef, string Name, string? AliasHint);

/// <summary>
/// Works out the Quickbase ids of an application of the import file: its app id from meta.QuickbaseAppCatalog (by logical id)
/// or the one the user typed, and each table's id from Quickbase's "get tables" API, matched on the table alias
/// (file <c>AliasHint</c> ⇔ API <c>alias</c>, ignoring case).
/// </summary>
public sealed class QuickbaseIdResolver
{
    private readonly IQuickbaseTablesApi _api;
    private readonly IQuickbaseCatalogRepository _catalog;

    public QuickbaseIdResolver(IQuickbaseTablesApi api, IQuickbaseCatalogRepository catalog)
    {
        _api = api;
        _catalog = catalog;
    }

    public async Task<QuickbaseAppIds> ResolveAsync(string appLogicalId, IReadOnlyList<QuickbaseFileTable> tables,
        string? manualAppId = null, string? userToken = null, CancellationToken ct = default)
    {
        var catalog = string.IsNullOrWhiteSpace(appLogicalId) ? null : await _catalog.FindAppByLogicalIdAsync(appLogicalId, ct);
        var manual = string.IsNullOrWhiteSpace(manualAppId) ? null : manualAppId.Trim();
        var qbAppId = manual ?? catalog?.QbAppId;

        var result = new QuickbaseAppIds
        {
            QbAppId = qbAppId,
            QbAppLogicalId = appLogicalId,
            AppIdSource = manual is not null && !string.Equals(manual, catalog?.QbAppId, StringComparison.OrdinalIgnoreCase) ? "Manual"
                : qbAppId is not null ? "Catalog" : "Missing",
            Tables = tables.Select(t => new QuickbaseTableIds { LogicalRef = t.LogicalRef, Name = t.Name, AliasHint = t.AliasHint }).ToList(),
        };

        if (qbAppId is null)
        {
            result.Message = "This app is not in the Quickbase app list. Enter its Quickbase app id.";
            return result;
        }

        var lookup = await _api.GetTablesAsync(qbAppId, userToken, ct);
        result.TokenStatus = lookup.Status.ToString();
        result.Message = lookup.Message;
        if (lookup.Status != QuickbaseLookupStatus.Ok)
            return result;

        Match(result.Tables, lookup.Tables);
        var unmatched = result.Tables.Count - result.MatchedTableCount;
        if (unmatched > 0)
            result.Message = $"{unmatched} table(s) of the file have no matching alias in Quickbase; their Quickbase table id stays empty.";
        return result;
    }

    /// <summary>Alias first (the requested rule). A file table without an alias falls back to an exact, unique name match.</summary>
    public static void Match(List<QuickbaseTableIds> fileTables, IReadOnlyList<QuickbaseTable> qbTables)
    {
        var byAlias = qbTables.Where(t => !string.IsNullOrWhiteSpace(t.Alias))
            .GroupBy(t => t.Alias!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byName = qbTables.Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var table in fileTables)
        {
            table.QbTableId = null;
            if (!string.IsNullOrWhiteSpace(table.AliasHint))
            {
                if (byAlias.TryGetValue(table.AliasHint.Trim(), out var hit))
                    table.QbTableId = hit.Id;
            }
            else if (byName.TryGetValue(table.Name.Trim(), out var named))
                table.QbTableId = named.Id;
        }
    }
}
