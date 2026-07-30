namespace PowerBase.Application.Pages.Queries.ListPages;

/// <summary>AllPages=true is the App-Settings authoring list (sees drafts + every visibility);
/// false is the role-visibility-filtered list used by ordinary members.</summary>
public record ListPagesQuery(Guid AppPublicId, string? Search, bool AllPages);
