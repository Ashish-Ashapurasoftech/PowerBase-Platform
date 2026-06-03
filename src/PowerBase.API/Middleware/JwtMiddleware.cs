using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Middleware;

public class JwtMiddleware
{
    private readonly RequestDelegate _next;

    public JwtMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IJwtService jwtService, IQueryContext queryContext, IUserPermissionRepository permissionRepo)
    {
        var token = context.Request.Headers["Authorization"]
            .FirstOrDefault()?.Split(" ").Last();

        if (token is not null && jwtService.ValidateToken(token, out var userId, out var tenantId, out _, out var userName, out var userEmail, out var systemRoleCode))
        {
            var ctx = (QueryContext)queryContext;
            ctx.UserId      = userId;
            ctx.TenantId    = tenantId;
            ctx.IsSuperAdmin  = systemRoleCode == SystemRoleCodes.SuperAdmin;
            ctx.UserName    = userName;
            ctx.UserEmail   = userEmail;
            ctx.IpAddress   = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
            if (tenantId > 0)
                ctx.Permissions = await permissionRepo.GetPermissionsAsync(userId, tenantId);
        }

        await _next(context);
    }
}
