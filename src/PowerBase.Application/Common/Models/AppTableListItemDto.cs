namespace PowerBase.Application.Common.Models;

/// <summary>Slim projection of <see cref="PowerBase.Domain.Entities.AppTable"/> for list views —
/// excludes Fields and other heavy/internal columns not needed by the tables listing UI.</summary>
public class AppTableListItemDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SingularLabel { get; set; }
    public string? Icon { get; set; }
    public int RecordCount { get; set; }
    public int FieldCount { get; set; }
    public bool IsShowInBar { get; set; }
    public DateTime CreatedOn { get; set; }
}
