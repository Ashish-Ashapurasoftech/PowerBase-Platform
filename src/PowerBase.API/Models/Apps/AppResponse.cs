using PowerBase.Domain.ValueObjects;

namespace PowerBase.API.Models.Apps;

public class AppResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? Color { get; init; }
    public AppFormattingSettings? Formatting { get; init; }
    public AppSecurityOptionsSettings? SecurityOptions { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedOn { get; init; }
    public string? OwnerName { get; init; }
    public bool IsEncrypted { get; init; }
}
