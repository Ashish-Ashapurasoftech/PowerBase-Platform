namespace PowerBase.Application.Pages.Queries.GetPageCode;

/// <summary>AppPublicId + Token are only needed to REWRITE sibling css/js links inside HTML
/// content into their own full served URLs (see GetPageCodeQueryHandler) — Token is forwarded
/// into each rewritten link so the browser's follow-up request for that sibling is already
/// authenticated, same simplified/non-cookie MVP path as the page's own serving URL.</summary>
public record GetPageCodeQuery(Guid PagePublicId, Guid AppPublicId, string? Token);
