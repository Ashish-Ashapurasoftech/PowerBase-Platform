using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class PageRepository : TenantRepositoryBase, IPageRepository
{
    private const string SelectColumns = """
        p.Id, p.PublicId, p.AppId, p.PageNumber, p.PageType, p.Name, p.Description, p.OwnerId,
        p.Visibility, p.Definition, p.ContentType, p.CodeHtml, p.CodeCss, p.CodeJs,
        p.IsPublished, p.CurrentVersionNo, p.PublishedVersionNo,
        p.ShowInNav, p.NavOrder, p.NavIcon, p.IsDefaultHome,
        p.IsDeleted, p.CreatedOn, p.CreatedBy, p.ModifiedOn, p.ModifiedBy, p.RowVersion
        """;

    private const string VisibilityPredicate = """
        (
            p.Visibility = 'Shared'
            OR (p.Visibility = 'Personal' AND p.OwnerId = @userId)
            OR (p.Visibility IN ('MyRole', 'SpecificRoles') AND EXISTS (
                SELECT 1 FROM meta.AppRolePage arp
                JOIN meta.AppUser au ON au.AppRoleId = arp.AppRoleId
                WHERE arp.PageId = p.Id AND au.UserId = @userId AND au.IsDeleted = 0
            ))
        )
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.Page p
        WHERE p.PublicId = @publicId AND p.IsDeleted = 0
        """;

    private const string GetVisiblePageSql = $"""
        SELECT {SelectColumns}
        FROM meta.Page p
        WHERE p.PublicId = @publicId AND p.IsDeleted = 0 AND {VisibilityPredicate}
        """;

    private const string GetVisiblePageByNumberSql = $"""
        SELECT {SelectColumns}
        FROM meta.Page p
        JOIN meta.App a ON a.Id = p.AppId
        WHERE a.PublicId = @appPublicId AND p.PageNumber = @pageNumber
          AND p.IsDeleted = 0 AND {VisibilityPredicate}
        """;

    private const string GetAppIdByPublicIdSql = """
        SELECT AppId FROM meta.Page WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string ListVisibleByAppSql = $"""
        SELECT {SelectColumns}
        FROM meta.Page p
        WHERE p.AppId = @appId AND p.IsDeleted = 0 AND {VisibilityPredicate}
          AND (@search IS NULL OR p.Name LIKE '%' + @search + '%')
        ORDER BY p.NavOrder, p.Name
        """;

    private const string ListAllByAppSql = $"""
        SELECT {SelectColumns}
        FROM meta.Page p
        WHERE p.AppId = @appId AND p.IsDeleted = 0
          AND (@search IS NULL OR p.Name LIKE '%' + @search + '%')
        ORDER BY p.NavOrder, p.Name
        """;

    private const string ListNavPagesSql = $"""
        SELECT {SelectColumns}
        FROM meta.Page p
        WHERE p.AppId = @appId AND p.IsDeleted = 0 AND p.ShowInNav = 1 AND p.IsPublished = 1
          AND {VisibilityPredicate}
        ORDER BY p.NavOrder, p.Name
        """;

    private const string InsertSql = """
        INSERT INTO meta.Page
            (AppId, PageNumber, PageType, Name, Description, OwnerId, Visibility, Definition,
             ContentType, CodeHtml, CodeCss, CodeJs, ShowInNav, NavOrder, NavIcon,
             IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId, INSERTED.PageNumber
        VALUES
            (@appId,
             ISNULL((SELECT MAX(p2.PageNumber) FROM meta.Page p2 WITH (UPDLOCK, HOLDLOCK) WHERE p2.AppId = @appId), 0) + 1,
             @pageType, @name, @description, @ownerId, @visibility, @definition,
             @contentType, @codeHtml, @codeCss, @codeJs, @showInNav, @navOrder, @navIcon,
             0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdateSql = """
        UPDATE meta.Page
        SET Name = @name, Description = @description, Visibility = @visibility,
            Definition = @definition, ContentType = @contentType,
            CodeHtml = @codeHtml, CodeCss = @codeCss, CodeJs = @codeJs,
            ShowInNav = @showInNav, NavOrder = @navOrder, NavIcon = @navIcon,
            CurrentVersionNo = @currentVersionNo,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string SoftDeleteManySql = """
        UPDATE meta.Page
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE PublicId IN @publicIds AND IsDeleted = 0
        """;

    private const string SetPublishedSql = """
        UPDATE meta.Page
        SET IsPublished = @isPublished, PublishedVersionNo = @publishedVersionNo,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string UnsetDefaultHomeSql = """
        UPDATE meta.Page SET IsDefaultHome = 0, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE AppId = @appId AND IsDefaultHome = 1 AND IsDeleted = 0
        """;

    private const string SetDefaultHomeSql = """
        UPDATE meta.Page SET IsDefaultHome = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string InsertVersionSql = """
        INSERT INTO meta.PageVersion
            (PageId, VersionNo, PageType, Name, Description, Definition, CodeHtml, CodeCss, CodeJs,
             ChangeNotes, WasPublished, CreatedOn, CreatedBy)
        VALUES
            (@pageId, @versionNo, @pageType, @name, @description, @definition, @codeHtml, @codeCss, @codeJs,
             @changeNotes, @wasPublished, SYSUTCDATETIME(), @createdBy)
        """;

    private const string ListVersionsSql = """
        SELECT pv.Id, pv.PublicId, pv.PageId, pv.VersionNo, pv.PageType, pv.Name, pv.Description,
               pv.Definition, pv.CodeHtml, pv.CodeCss, pv.CodeJs, pv.ChangeNotes, pv.WasPublished,
               pv.CreatedOn, pv.CreatedBy
        FROM meta.PageVersion pv
        JOIN meta.Page p ON p.Id = pv.PageId
        WHERE p.PublicId = @pagePublicId
        ORDER BY pv.VersionNo DESC
        """;

    private const string GetVersionSql = """
        SELECT pv.Id, pv.PublicId, pv.PageId, pv.VersionNo, pv.PageType, pv.Name, pv.Description,
               pv.Definition, pv.CodeHtml, pv.CodeCss, pv.CodeJs, pv.ChangeNotes, pv.WasPublished,
               pv.CreatedOn, pv.CreatedBy
        FROM meta.PageVersion pv
        JOIN meta.Page p ON p.Id = pv.PageId
        WHERE p.PublicId = @pagePublicId AND pv.VersionNo = @versionNo
        """;

    public PageRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<Page> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var page = await connection.QuerySingleOrDefaultAsync<Page>(
            new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
        return page ?? throw new NotFoundException("Page", publicId);
    }

    public async Task<Page?> GetVisiblePageAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Page>(
            new CommandDefinition(GetVisiblePageSql, new { publicId, userId = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task<Page?> GetVisiblePageByNumberAsync(Guid appPublicId, int pageNumber, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Page>(
            new CommandDefinition(GetVisiblePageByNumberSql,
                new { appPublicId, pageNumber, userId = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task<long> GetAppIdByPublicIdAsync(Guid pagePublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var appId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetAppIdByPublicIdSql, new { publicId = pagePublicId }, cancellationToken: ct));
        return appId ?? throw new NotFoundException("Page", pagePublicId);
    }

    public async Task<IReadOnlyList<Page>> ListVisibleByAppAsync(long appId, string? search, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Page>(
            new CommandDefinition(ListVisibleByAppSql, new { appId, userId = QueryContext.UserId, search }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<Page>> ListAllByAppAsync(long appId, string? search, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Page>(
            new CommandDefinition(ListAllByAppSql, new { appId, search }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<Page>> ListNavPagesAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Page>(
            new CommandDefinition(ListNavPagesSql, new { appId, userId = QueryContext.UserId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<(long Id, Guid PublicId, int PageNumber)> CreateAsync(Page page, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var row = await connection.QuerySingleAsync(
                new CommandDefinition(InsertSql, new
                {
                    appId = page.AppId,
                    pageType = page.PageType,
                    name = page.Name,
                    description = page.Description,
                    ownerId = page.OwnerId,
                    visibility = page.Visibility,
                    definition = page.Definition,
                    contentType = page.ContentType,
                    codeHtml = page.CodeHtml,
                    codeCss = page.CodeCss,
                    codeJs = page.CodeJs,
                    showInNav = page.ShowInNav,
                    navOrder = page.NavOrder,
                    navIcon = page.NavIcon,
                    createdBy = QueryContext.UserId,
                }, transaction: transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            return ((long)row.Id, (Guid)row.PublicId, (int)row.PageNumber);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task UpdateAsync(Page page, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(UpdateSql, new
            {
                publicId = page.PublicId,
                name = page.Name,
                description = page.Description,
                visibility = page.Visibility,
                definition = page.Definition,
                contentType = page.ContentType,
                codeHtml = page.CodeHtml,
                codeCss = page.CodeCss,
                codeJs = page.CodeJs,
                showInNav = page.ShowInNav,
                navOrder = page.NavOrder,
                navIcon = page.NavIcon,
                currentVersionNo = page.CurrentVersionNo,
                modifiedBy = QueryContext.UserId,
            }, cancellationToken: ct));
        if (affected == 0) throw new NotFoundException("Page", page.PublicId);
    }

    public async Task SoftDeleteManyAsync(IReadOnlyList<Guid> publicIds, CancellationToken ct = default)
    {
        if (publicIds.Count == 0) return;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteManySql, new { publicIds, deletedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId, int PageNumber)> DuplicateAsync(Guid sourcePublicId, string newName, CancellationToken ct = default)
    {
        var source = await GetByPublicIdAsync(sourcePublicId, ct);
        var copy = new Page
        {
            AppId = source.AppId,
            PageType = source.PageType,
            Name = newName,
            Description = source.Description,
            OwnerId = QueryContext.UserId,
            // Duplicates default to Personal regardless of the source's visibility — a
            // deliberate safety default so copying a Shared/role-visible page doesn't silently
            // publish the copy to the same audience before the duplicating user reviews it.
            Visibility = Domain.Enums.Visibility.Personal.ToString(),
            Definition = source.Definition,
            ContentType = source.ContentType,
            CodeHtml = source.CodeHtml,
            CodeCss = source.CodeCss,
            CodeJs = source.CodeJs,
            ShowInNav = false,
            NavOrder = source.NavOrder,
            NavIcon = source.NavIcon,
        };
        return await CreateAsync(copy, ct);
    }

    public async Task SetPublishedAsync(Guid publicId, bool isPublished, int? publishedVersionNo, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SetPublishedSql,
                new { publicId, isPublished, publishedVersionNo, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0) throw new NotFoundException("Page", publicId);
    }

    public async Task SetDefaultHomeAsync(long appId, Guid? publicId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(UnsetDefaultHomeSql, new { appId, modifiedBy = QueryContext.UserId }, transaction: transaction, cancellationToken: ct));
            if (publicId.HasValue)
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(SetDefaultHomeSql, new { publicId = publicId.Value, modifiedBy = QueryContext.UserId }, transaction: transaction, cancellationToken: ct));
            }
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task InsertVersionAsync(PageVersion version, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(InsertVersionSql, new
            {
                pageId = version.PageId,
                versionNo = version.VersionNo,
                pageType = version.PageType,
                name = version.Name,
                description = version.Description,
                definition = version.Definition,
                codeHtml = version.CodeHtml,
                codeCss = version.CodeCss,
                codeJs = version.CodeJs,
                changeNotes = version.ChangeNotes,
                wasPublished = version.WasPublished,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PageVersion>> ListVersionsAsync(Guid pagePublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<PageVersion>(
            new CommandDefinition(ListVersionsSql, new { pagePublicId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<PageVersion?> GetVersionAsync(Guid pagePublicId, int versionNo, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PageVersion>(
            new CommandDefinition(GetVersionSql, new { pagePublicId, versionNo }, cancellationToken: ct));
    }

    public async Task<Dictionary<long, List<long>>> GetAppRolePagesMapAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<(long PageId, long AppRoleId)>(
            new CommandDefinition("""
                SELECT arp.PageId, arp.AppRoleId
                FROM meta.AppRolePage arp
                JOIN meta.Page p ON p.Id = arp.PageId
                WHERE p.AppId = @appId AND p.IsDeleted = 0
                """, new { appId }, cancellationToken: ct));
        return results.GroupBy(x => x.PageId)
                      .ToDictionary(g => g.Key, g => g.Select(x => x.AppRoleId).ToList());
    }

    public async Task ReplacePageRolesAsync(long pageId, IEnumerable<long> roleIds, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition("DELETE FROM meta.AppRolePage WHERE PageId = @pageId", new { pageId }, transaction: transaction, cancellationToken: ct));
            var roleIdList = roleIds.ToList();
            if (roleIdList.Count > 0)
            {
                var parameters = roleIdList.Select(roleId => new { pageId, roleId }).ToList();
                await connection.ExecuteAsync(
                    new CommandDefinition("INSERT INTO meta.AppRolePage (PageId, AppRoleId) VALUES (@pageId, @roleId)", parameters, transaction: transaction, cancellationToken: ct));
            }
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<IReadOnlyList<long>> GetPageRoleIdsAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<long>(
            new CommandDefinition("SELECT AppRoleId FROM meta.AppRolePage WHERE PageId = @pageId", new { pageId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<Guid>> GetPageRolePublicIdsAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Guid>(
            new CommandDefinition("""
                SELECT ar.PublicId
                FROM meta.AppRolePage arp
                JOIN meta.AppRole ar ON ar.Id = arp.AppRoleId
                WHERE arp.PageId = @pageId
                """, new { pageId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<Dictionary<long, List<string>>> GetHomePageRoleNamesAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<(long PageId, string RoleName)>(
            new CommandDefinition("""
                SELECT ar.HomePageId AS PageId, ar.Name AS RoleName
                FROM meta.AppRole ar
                WHERE ar.AppId = @appId AND ar.HomePageId IS NOT NULL AND ar.IsDeleted = 0
                """, new { appId }, cancellationToken: ct));
        return results.GroupBy(x => x.PageId)
                      .ToDictionary(g => g.Key, g => g.Select(x => x.RoleName).ToList());
    }
}
