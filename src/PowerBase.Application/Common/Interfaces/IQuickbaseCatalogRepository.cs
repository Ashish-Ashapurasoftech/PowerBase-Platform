namespace PowerBase.Application.Common.Interfaces;

/// <summary>A Quickbase user token (meta.QuickbaseUserToken, migration 099).</summary>
public sealed class QuickbaseUserTokenRow
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>A Quickbase app that is imported (meta.QuickbaseAppCatalog, migration 099).</summary>
public sealed class QuickbaseCatalogAppRow
{
    public long Id { get; set; }
    /// <summary>Quickbase app id, e.g. <c>bunx343v9</c>.</summary>
    public string QbAppId { get; set; } = string.Empty;
    /// <summary>Logical id in the QBL file, e.g. <c>$App_HIH_Archive</c>.</summary>
    public string LogicalId { get; set; } = string.Empty;
    public string? Solution { get; set; }
    /// <summary>The user token with access to this app; null = unknown, every active token is tried.</summary>
    public long? UserTokenId { get; set; }
    public string? UserTokenName { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>The Quickbase app catalog and user tokens the import uses, managed from the hidden admin page.</summary>
public interface IQuickbaseCatalogRepository
{
    Task<IReadOnlyList<QuickbaseUserTokenRow>> ListTokensAsync(CancellationToken ct = default);
    Task<QuickbaseUserTokenRow?> GetTokenAsync(long id, CancellationToken ct = default);
    Task<long> CreateTokenAsync(string name, string token, int sortOrder, bool isActive, CancellationToken ct = default);
    /// <summary><paramref name="token"/> null = keep the stored token.</summary>
    Task<bool> UpdateTokenAsync(long id, string name, string? token, int sortOrder, bool isActive, CancellationToken ct = default);
    Task<bool> DeleteTokenAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<QuickbaseCatalogAppRow>> ListAppsAsync(CancellationToken ct = default);
    Task<QuickbaseCatalogAppRow?> GetAppAsync(long id, CancellationToken ct = default);
    /// <summary>By logical id (e.g. <c>$App_HIH_Archive</c>), ignoring case.</summary>
    Task<QuickbaseCatalogAppRow?> FindAppByLogicalIdAsync(string logicalId, CancellationToken ct = default);
    /// <summary>By Quickbase app id (e.g. <c>bunx343v9</c>), ignoring case.</summary>
    Task<QuickbaseCatalogAppRow?> FindAppByQbAppIdAsync(string qbAppId, CancellationToken ct = default);
    Task<long> CreateAppAsync(string qbAppId, string logicalId, string? solution, long? userTokenId, CancellationToken ct = default);
    Task<bool> UpdateAppAsync(long id, string qbAppId, string logicalId, string? solution, long? userTokenId, CancellationToken ct = default);
    Task<bool> DeleteAppAsync(long id, CancellationToken ct = default);
}
