namespace PowerBase.Application.Users.Queries.ListUsers;

public record ListUsersQuery(string? SearchTerm, string? RoleName, string? Status);
