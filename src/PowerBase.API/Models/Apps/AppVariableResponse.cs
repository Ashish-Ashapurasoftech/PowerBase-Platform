namespace PowerBase.API.Models.Apps;

public class AppVariableResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? ModifiedOn { get; init; }
}
