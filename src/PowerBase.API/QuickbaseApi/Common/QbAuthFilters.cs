using Microsoft.AspNetCore.Mvc.Filters;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// Authentication for Quickbase-compatible REST endpoints. Identity is resolved by the existing JwtMiddleware,
/// which already accepts <c>Authorization: QB-USER-TOKEN pb_ut_...</c> (it takes the last space-separated part)
/// as well as <c>Bearer &lt;jwt&gt;</c>. This filter only answers 401 in Quickbase's error format.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class QbRequireAuthAttribute : ActionFilterAttribute
{
    public QbRequireAuthAttribute() => Order = -100;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var identity = context.HttpContext.RequestServices.GetRequiredService<IQueryContext>();
        if (identity.UserId <= 0 || identity.TenantId <= 0)
            context.Result = QbErrorBody.Result(401, "Unauthorized",
                "Missing or invalid Authorization header. Use 'QB-USER-TOKEN <user token>'.");
    }
}

/// <summary>
/// Optional <c>QB-Realm-Hostname</c> check (user decision 2026-11-04: optional). When the header is present it must
/// name the caller's tenant: either the slug itself or a hostname whose first label is the slug
/// (e.g. slug "demo" accepts "demo" and "demo.quickbase.com"). When absent, the token's tenant is used.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class QbRealmHostnameAttribute : ActionFilterAttribute
{
    public const string HEADER_NAME = "QB-Realm-Hostname";

    public QbRealmHostnameAttribute() => Order = -90;

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var realm = context.HttpContext.Request.Headers[HEADER_NAME].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(realm))
        {
            var identity = context.HttpContext.RequestServices.GetRequiredService<IQueryContext>();
            if (identity.TenantId > 0)
            {
                var tenants = context.HttpContext.RequestServices.GetRequiredService<ITenantRepository>();
                var tenant = await tenants.GetByIdAsync(identity.TenantId, context.HttpContext.RequestAborted);
                if (!RealmMatches(realm, tenant.Slug))
                {
                    context.Result = QbErrorBody.Result(401, "Unauthorized",
                        $"Realm '{realm.Trim()}' does not match the authenticated account.");
                    return;
                }
            }
        }
        await next();
    }

    public static bool RealmMatches(string realm, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return false;
        var value = realm.Trim();
        if (value.Equals(slug, StringComparison.OrdinalIgnoreCase)) return true;
        var firstLabel = value.Split('.', 2)[0];
        return firstLabel.Equals(slug, StringComparison.OrdinalIgnoreCase);
    }
}
