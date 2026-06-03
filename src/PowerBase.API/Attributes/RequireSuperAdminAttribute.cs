using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireSuperAdminAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var queryContext = context.HttpContext.RequestServices.GetRequiredService<IQueryContext>();
        if (queryContext.UserId == 0)
        {
            context.Result = new ObjectResult(new
            {
                error = new { code = "UNAUTHORIZED", message = "Authentication required." }
            })
            { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        if (!queryContext.IsSuperAdmin)
        {
            context.Result = new ObjectResult(new
            {
                error = new { code = "FORBIDDEN", message = "Super-admin access required." }
            })
            { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}
