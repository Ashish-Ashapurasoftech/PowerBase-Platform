using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.Attributes;

public enum AppAccessResolver { ByAppId, ByAppPublicId, ByTableId, ByTablePublicId, ByReportPublicId, ByFormPublicId, ByFormRulePublicId }

[AttributeUsage(AttributeTargets.Method)]
public class RequireAppAccessAttribute : Attribute, IFilterFactory
{
    private readonly AppAccess _required;
    private readonly AppAccessResolver _resolver;

    public RequireAppAccessAttribute(AppAccess required, AppAccessResolver resolver)
    {
        _required = required;
        _resolver = resolver;
    }

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new AppAccessFilter(serviceProvider.GetRequiredService<IAppAccessService>(), _required, _resolver);
}

internal class AppAccessFilter : IAsyncActionFilter
{
    private readonly IAppAccessService _accessService;
    private readonly AppAccess _required;
    private readonly AppAccessResolver _resolver;

    public AppAccessFilter(IAppAccessService accessService, AppAccess required, AppAccessResolver resolver)
    {
        _accessService = accessService;
        _required = required;
        _resolver = resolver;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var route = context.RouteData.Values;

        try
        {
            switch (_resolver)
            {
                case AppAccessResolver.ByAppId:
                    var appId = Guid.Parse(route["appId"]!.ToString()!);
                    await _accessService.RequireByAppPublicIdAsync(appId, _required, context.HttpContext.RequestAborted);
                    break;

                case AppAccessResolver.ByAppPublicId:
                    var appPubId = Guid.Parse(route["publicId"]!.ToString()!);
                    await _accessService.RequireByAppPublicIdAsync(appPubId, _required, context.HttpContext.RequestAborted);
                    break;

                case AppAccessResolver.ByTableId:
                    var tableId = Guid.Parse(route["tableId"]!.ToString()!);
                    await _accessService.RequireByTablePublicIdAsync(tableId, _required, context.HttpContext.RequestAborted);
                    break;

                case AppAccessResolver.ByTablePublicId:
                    var tablePubId = Guid.Parse(route["publicId"]!.ToString()!);
                    await _accessService.RequireByTablePublicIdAsync(tablePubId, _required, context.HttpContext.RequestAborted);
                    break;

                case AppAccessResolver.ByReportPublicId:
                    var reportPubId = Guid.Parse(route["publicId"]!.ToString()!);
                    await _accessService.RequireByReportPublicIdAsync(reportPubId, _required, context.HttpContext.RequestAborted);
                    break;

                case AppAccessResolver.ByFormPublicId:
                    var formPubId = Guid.Parse(route["publicId"]!.ToString()!);
                    await _accessService.RequireByFormPublicIdAsync(formPubId, _required, context.HttpContext.RequestAborted);
                    break;

                case AppAccessResolver.ByFormRulePublicId:
                    var rulePubId = Guid.Parse(route["ruleId"]!.ToString()!);
                    await _accessService.RequireByFormRulePublicIdAsync(rulePubId, _required, context.HttpContext.RequestAborted);
                    break;
            }
        }
        catch (UnauthorizedActionException ex)
        {
            context.Result = new ObjectResult(new
            {
                error = new { code = "FORBIDDEN", message = ex.Message }
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
        catch (NotFoundException ex)
        {
            context.Result = new ObjectResult(new
            {
                error = new { code = "NOT_FOUND", message = ex.Message }
            })
            { StatusCode = StatusCodes.Status404NotFound };
            return;
        }

        await next();
    }
}
