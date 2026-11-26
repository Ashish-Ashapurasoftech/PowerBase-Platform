using PowerBase.Application.Relationships;

namespace PowerBase.API.Models.Relationships;

/// <param name="HasMore">More options follow: ask again with skip = options loaded so far (scroll to load more).</param>
public record ParentOptionsResponse(IReadOnlyList<string> Headers, IReadOnlyList<ReferenceOption> Options, bool HasMore = false);
