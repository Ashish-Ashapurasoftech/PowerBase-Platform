namespace PowerBase.Application.Relationships.Queries;

/// <param name="HasMore">More options follow this page: the picker loads the next page (skip + this page's count) on scroll.</param>
public record GetParentOptionsResult(IReadOnlyList<string> Headers, IReadOnlyList<ReferenceOption> Options, bool HasMore = false);
