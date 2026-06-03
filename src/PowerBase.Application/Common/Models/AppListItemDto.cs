namespace PowerBase.Application.Common.Models;

using PowerBase.Domain.Entities;

public class AppListItemDto : App
{
    public new string? OwnerName { get; set; }
}
