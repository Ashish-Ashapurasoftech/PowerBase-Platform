using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Queries.ListAppUsers;

public record AppUserResult(
    Guid PublicId,
    Guid UserPublicId,
    string UserName,
    string UserEmail,
    Guid RolePublicId,
    string RoleName,
    string Status,
    bool ShowInUserPickers,
    DateTime AddedOn,
    bool IsOwner,
    bool IsFromGroup,
    string? GroupName);

public class ListAppUsersResult
{
    public IReadOnlyList<AppUserResult> Items { get; init; } = Array.Empty<AppUserResult>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ListAppUsersQueryHandler
{
    private static readonly HashSet<string> AllowedSortFields =
        new(StringComparer.OrdinalIgnoreCase) { "userName", "userEmail", "roleName", "addedOn", "accessVia" };

    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;

    public ListAppUsersQueryHandler(IAppRepository appRepo, IAppUserRepository appUserRepo)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
    }

    public async Task<ListAppUsersResult> HandleAsync(ListAppUsersQuery query, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(query.AppPublicId, ct);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;
        var sortBy = AllowedSortFields.Contains(query.SortBy) ? query.SortBy : "userName";

        var roles = query.Roles?.ToList() ?? new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Role) && !roles.Contains(query.Role, StringComparer.OrdinalIgnoreCase))
        {
            roles.Add(query.Role);
        }

        IReadOnlyList<AppUserDetail> users;
        int total;

        if (query.IsExport)
        {
            users = await _appUserRepo.ListByAppFilteredAsync(
                app.Id,
                query.Search,
                roles,
                query.AccessTypes,
                query.UserPickerFilters,
                query.Groups,
                sortBy,
                query.SortDesc,
                ct);
            total = users.Count;
        }
        else
        {
            users = await _appUserRepo.ListByAppPagedAsync(
                app.Id,
                page,
                pageSize,
                query.Search,
                roles,
                query.AccessTypes,
                query.UserPickerFilters,
                query.Groups,
                sortBy,
                query.SortDesc,
                ct);
            total = await _appUserRepo.CountByAppAsync(
                app.Id,
                query.Search,
                roles,
                query.AccessTypes,
                query.UserPickerFilters,
                query.Groups,
                ct);
        }

        var items = users.Select(u => new AppUserResult(
            u.PublicId,
            u.UserPublicId,
            u.UserName,
            u.UserEmail,
            u.RolePublicId,
            u.RoleName,
            u.Status,
            u.ShowInUserPickers,
            u.CreatedOn,
            u.IsOwner,
            u.IsFromGroup,
            u.GroupName)).ToList();

        return new ListAppUsersResult
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }
}
