namespace PowerBase.API.Models.Pages;

public class NavPageResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PageType { get; set; } = string.Empty;
    public string? NavIcon { get; set; }
    public int PageNumber { get; set; }
}
