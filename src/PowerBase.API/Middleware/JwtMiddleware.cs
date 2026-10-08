using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Middleware;

public class JwtMiddleware
{
    private readonly RequestDelegate _next;

    public JwtMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context, 
        IJwtService jwtService, 
        IQueryContext queryContext, 
        IUserPermissionRepository permissionRepo,
        IUserTokenRepository userTokenRepository,
        IUserRepository userRepository,
        ITenantRepository tenantRepository,
        IAuditRepository auditRepository)
    {
        // Falls back to a `?token=` query param when there's no Authorization header — needed for
        // Code Pages: the served page is a plain document navigation/new-tab open, not an
        // XHR/fetch the SPA's interceptor can attach a header to. The launcher route that opens a
        // Code Page builds this query param from the SPA's own stored token immediately before
        // opening it (see PagesController's code-serving endpoint) — this is the simplified,
        // non-cookie MVP path; not intended as this app's general auth story.
        var token = context.Request.Headers["Authorization"]
            .FirstOrDefault()?.Split(" ").Last()
            ?? context.Request.Query["token"].FirstOrDefault();

        if (token is not null)
        {
            if (token.StartsWith("pb_ut_"))
            {
                using var sha256 = SHA256.Create();
                var hash = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

                var userToken = await userTokenRepository.GetByHashAsync(hash, context.RequestAborted);
                if (userToken != null && userToken.IsActive && !userToken.IsDeleted)
                {
                    var user = await userRepository.GetByIdAsync(userToken.UserId, context.RequestAborted);
                    if (user != null && user.IsActive && !user.IsDeleted)
                    {
                        var ctx = (QueryContext)queryContext;
                        ctx.UserId = user.Id;
                        ctx.TenantId = userToken.TenantId;
                        ctx.IsSuperAdmin = user.SystemRoleCode == SystemRoleCodes.SuperAdmin;
                        ctx.UserName = user.Name;
                        ctx.UserEmail = user.Email;
                        ctx.IpAddress = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
                        ctx.IsUserToken = true;
                        ctx.TokenAccessAllApps = userToken.AccessAllApps;

                        if (!userToken.AccessAllApps)
                        {
                            ctx.AllowedAppIds = await userTokenRepository.GetAllowedAppIdsAsync(userToken.Id, context.RequestAborted);
                        }

                        if (userToken.TenantId > 0)
                        {
                            ctx.Permissions = await permissionRepo.GetPermissionsAsync(user.Id, userToken.TenantId);
                            ctx.TenantRole = await tenantRepository.GetUserRoleNameInTenantAsync(user.Id, userToken.TenantId, context.RequestAborted) ?? string.Empty;
                        }

                        // Fire and forget update last used at to prevent blocking request thread
                        _ = userTokenRepository.UpdateLastUsedAtAsync(userToken.Id, CancellationToken.None);
                    }
                }
            }
            else if (jwtService.ValidateToken(token, out var userId, out var tenantId, out var jwtId, out var userName, out var userEmail, out var systemRoleCode, out var tenantRole)
                     // A logged-out token is still cryptographically valid until `exp`; leaving the context
                     // unpopulated makes [RequireAuth] answer 401, same as an expired token.
                     && !await auditRepository.IsSessionRevokedAsync(jwtId, context.RequestAborted))
            {
                var ctx = (QueryContext)queryContext;
                ctx.JwtId       = jwtId;
                ctx.UserId      = userId;
                ctx.TenantId    = tenantId;
                ctx.IsSuperAdmin  = systemRoleCode == SystemRoleCodes.SuperAdmin;
                ctx.UserName    = userName;
                ctx.UserEmail   = userEmail;
                ctx.IpAddress   = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
                ctx.TenantRole  = tenantRole;
                ctx.IsUserToken = false;
                ctx.TokenAccessAllApps = true;
                ctx.AllowedAppIds = new HashSet<long>();
                if (tenantId > 0)
                    ctx.Permissions = await permissionRepo.GetPermissionsAsync(userId, tenantId);
            }
        }

        await _next(context);
    }
}
