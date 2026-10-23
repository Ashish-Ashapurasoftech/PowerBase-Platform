using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PowerBase.Domain.ConnectedTables;

namespace PowerBase.API.ConnectedTables;

/// <summary>Answers the module's own errors (<see cref="ConnectedTableException"/>) with their status and code, so the global
/// exception middleware doesn't need to know about this module. Every other exception goes on to the middleware as usual.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ConnectedTablesExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not ConnectedTableException ex) return;
        context.Result = new ObjectResult(new { error = new { code = ex.ErrorCode, message = ex.Message } }) { StatusCode = ex.StatusCode };
        context.ExceptionHandled = true;
    }
}
