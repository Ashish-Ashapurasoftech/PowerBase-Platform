using System.Text.Json;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, code, message, errors) = exception switch
        {
            NotFoundException e => (StatusCodes.Status404NotFound, e.ErrorCode, e.Message, (object?)null),
            DuplicateException e => (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, (object?)null),
            UnauthorizedActionException e => (StatusCodes.Status403Forbidden, e.ErrorCode, e.Message, (object?)null),
            Domain.Exceptions.ValidationException e => (StatusCodes.Status400BadRequest, e.ErrorCode, e.Message, (object?)e.Errors),
            _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred.", (object?)null)
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode;

        object body;
        if (statusCode == StatusCodes.Status500InternalServerError && _env.IsDevelopment())
        {
            body = new { error = new { code, message = exception.Message, detail = exception.ToString() } };
        }
        else if (errors is not null)
        {
            body = new { error = new { code, message, errors } };
        }
        else
        {
            body = new { error = new { code, message } };
        }

        await context.Response.WriteAsync(JsonSerializer.Serialize(body,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
