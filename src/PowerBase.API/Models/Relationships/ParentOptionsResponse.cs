using PowerBase.Application.Relationships;

namespace PowerBase.API.Models.Relationships;

public record ParentOptionsResponse(IReadOnlyList<string> Headers, IReadOnlyList<ReferenceOption> Options);
