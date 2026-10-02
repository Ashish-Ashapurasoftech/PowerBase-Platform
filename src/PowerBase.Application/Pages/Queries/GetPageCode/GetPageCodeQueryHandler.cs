using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pages.Queries.GetPageCode;

public record PageCodeResult(string Content, string ContentType);

/// <summary>Resolves a Code page's single served file for the raw-content serving endpoint — its
/// Content field selected by ContentType (html/css/js), matching the "one Code Page = one file"
/// model: a page's ContentType picks which of Page.CodeHtml/CodeCss/CodeJs is actually authored
/// and served; the other two stay unused (deliberately no data-model/migration change — this is
/// a convention on top of the existing three-column shape). Reuses GetVisiblePageAsync so the
/// same per-role view permission Dashboard pages already enforce applies here too.</summary>
public class GetPageCodeQueryHandler
{
    private readonly IPageRepository _pageRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IQueryContext _queryContext;

    /// <summary>Matches a bare/relative href="…" or src="…" ending in .css/.js — deliberately
    /// excludes absolute URLs (http://, https://, protocol-relative //, data:) so an author's own
    /// CDN links (Bootstrap, jQuery, ...) are never touched, only same-app sibling references
    /// written the QuickBase way: a plain filename like "styles.css".</summary>
    private static readonly Regex SiblingLinkPattern = new(
        @"(?<attr>href|src)\s*=\s*(?<quote>[""'])(?<url>(?!https?://|//|data:)[^""'>]+\.(?:css|js))\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches the rename-proof alternative — a bare query string referencing the
    /// sibling's own per-app Page Id (the "PAGE ID" column in the Pages list), e.g.
    /// href="?pageId=28". Unlike the filename pattern, this survives the sibling page being
    /// renamed, at the cost of the author needing to know/paste that number instead of a name —
    /// same tradeoff QuickBase's own ?a=dbpage&pageid=N makes, just adapted to our path-based
    /// (not query-dispatched) serving URL, which is why this still needs rewriting at all (see
    /// GetPageCodeQueryHandler's own doc comment on why QuickBase's plain relative link doesn't
    /// carry over as-is).</summary>
    private static readonly Regex SiblingPageIdPattern = new(
        @"(?<attr>href|src)\s*=\s*(?<quote>[""'])\?pageId=(?<pageNumber>\d+)\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GetPageCodeQueryHandler(IPageRepository pageRepo, ITenantRepository tenantRepo, IQueryContext queryContext)
    {
        _pageRepo = pageRepo;
        _tenantRepo = tenantRepo;
        _queryContext = queryContext;
    }

    public async Task<PageCodeResult> HandleAsync(GetPageCodeQuery query, CancellationToken ct = default)
    {
        var page = await _pageRepo.GetVisiblePageAsync(query.PagePublicId, ct)
            ?? throw new NotFoundException("Page", query.PagePublicId);

        if (page.PageType != PageTypes.Code)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["pageType"] = ["Only Code pages can be served as raw content."]
            });

        // Tenant-level kill switch (Super Admin per-tenant toggle, Tenant.CodePagesEnabled) — a
        // page that was authored while the feature was on stops serving the moment a Super Admin
        // turns it off for this tenant, same as CreatePageCommandHandler/UpdatePageCommandHandler
        // block authoring it in the first place.
        var tenant = await _tenantRepo.GetByIdAsync(_queryContext.TenantId, ct);
        if (!tenant.CodePagesEnabled)
            throw new UnauthorizedActionException("Code Pages are not enabled for this tenant.");

        var contentType = (page.ContentType ?? "html").ToLowerInvariant();
        var content = contentType switch
        {
            "css" => page.CodeCss,
            "js" => page.CodeJs,
            _ => page.CodeHtml,
        } ?? string.Empty;

        // Sibling linking only applies to HTML referencing its own css/js — a CSS or JS page
        // isn't itself rewritten (matches the spec's "HTML references sibling css/js" wording;
        // CSS @import / JS module imports aren't in scope here).
        if (contentType == "html" && content.Length > 0)
            content = await RewriteSiblingLinksAsync(content, page.AppId, query.AppPublicId, query.Token, ct);

        return new PageCodeResult(content, contentType);
    }

    private async Task<string> RewriteSiblingLinksAsync(string html, long appId, Guid appPublicId, string? token, CancellationToken ct)
    {
        var hasFilenameRefs = SiblingLinkPattern.IsMatch(html);
        var hasPageIdRefs = SiblingPageIdPattern.IsMatch(html);
        if (!hasFilenameRefs && !hasPageIdRefs) return html;

        // One fetch serves both lookup strategies — every OTHER Code page in this app, keyed two
        // ways: by name (case-insensitive; first match wins if two pages somehow share a name —
        // names aren't enforced unique today) for the filename pattern, and by PageNumber (the
        // "PAGE ID" column, globally unique per app) for the rename-proof pattern.
        var appPages = await _pageRepo.ListVisibleByAppAsync(appId, null, ct);
        var codePages = appPages.Where(p => p.PageType == PageTypes.Code).ToList();
        var byName = codePages
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byNumber = codePages.ToDictionary(p => p.PageNumber, p => p);

        string BuildUrl(Page sibling)
        {
            var url = $"/apps/{appPublicId}/pages/{sibling.PublicId}/code/raw";
            if (!string.IsNullOrEmpty(token)) url += $"?token={Uri.EscapeDataString(token)}";
            return url;
        }

        if (hasFilenameRefs && byName.Count > 0)
        {
            html = SiblingLinkPattern.Replace(html, m =>
            {
                var referencedName = m.Groups["url"].Value;
                if (!byName.TryGetValue(referencedName, out var sibling))
                    return m.Value; // Not a recognized sibling — leave the author's markup untouched.
                return $"{m.Groups["attr"].Value}=\"{BuildUrl(sibling)}\"";
            });
        }

        if (hasPageIdRefs && byNumber.Count > 0)
        {
            html = SiblingPageIdPattern.Replace(html, m =>
            {
                if (!int.TryParse(m.Groups["pageNumber"].Value, out var pageNumber) || !byNumber.TryGetValue(pageNumber, out var sibling))
                    return m.Value; // Not a recognized sibling — leave the author's markup untouched.
                return $"{m.Groups["attr"].Value}=\"{BuildUrl(sibling)}\"";
            });
        }

        return html;
    }
}
