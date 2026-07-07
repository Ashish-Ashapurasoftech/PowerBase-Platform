namespace PowerBase.Application.Relationships.Queries;

public record GetParentOptionsResult(IReadOnlyList<string> Headers, IReadOnlyList<ReferenceOption> Options);
