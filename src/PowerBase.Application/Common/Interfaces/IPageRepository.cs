using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IPageRepository
{
    Task<Page> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<Page?> GetVisiblePageAsync(Guid publicId, CancellationToken ct = default);
    Task<Page?> GetVisiblePageByNumberAsync(Guid appPublicId, int pageNumber, CancellationToken ct = default);
    Task<long> GetAppIdByPublicIdAsync(Guid pagePublicId, CancellationToken ct = default);

    Task<IReadOnlyList<Page>> ListVisibleByAppAsync(long appId, string? search, CancellationToken ct = default);
    Task<IReadOnlyList<Page>> ListAllByAppAsync(long appId, string? search, CancellationToken ct = default);
    Task<IReadOnlyList<Page>> ListNavPagesAsync(long appId, CancellationToken ct = default);

    /// <summary>Inserts the page, allocating the next PageNumber for the app under a
    /// range lock (UPDLOCK, HOLDLOCK) so concurrent creates cannot collide.</summary>
    Task<(long Id, Guid PublicId, int PageNumber)> CreateAsync(Page page, CancellationToken ct = default);

    Task UpdateAsync(Page page, CancellationToken ct = default);
    Task SoftDeleteManyAsync(IReadOnlyList<Guid> publicIds, CancellationToken ct = default);
    Task<(long Id, Guid PublicId, int PageNumber)> DuplicateAsync(Guid sourcePublicId, string newName, CancellationToken ct = default);
    Task SetPublishedAsync(Guid publicId, bool isPublished, int? publishedVersionNo, CancellationToken ct = default);
    Task SetDefaultHomeAsync(long appId, Guid? publicId, CancellationToken ct = default);

    Task InsertVersionAsync(PageVersion version, CancellationToken ct = default);
    Task<IReadOnlyList<PageVersion>> ListVersionsAsync(Guid pagePublicId, CancellationToken ct = default);
    Task<PageVersion?> GetVersionAsync(Guid pagePublicId, int versionNo, CancellationToken ct = default);

    Task<Dictionary<long, List<long>>> GetAppRolePagesMapAsync(long appId, CancellationToken ct = default);
    Task ReplacePageRolesAsync(long pageId, IEnumerable<long> roleIds, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetPageRoleIdsAsync(long pageId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetPageRolePublicIdsAsync(long pageId, CancellationToken ct = default);

    /// <summary>Page Id → names of roles whose AppRole.HomePageId points at that page
    /// (the "Home page for" column in the Pages list).</summary>
    Task<Dictionary<long, List<string>>> GetHomePageRoleNamesAsync(long appId, CancellationToken ct = default);
}
