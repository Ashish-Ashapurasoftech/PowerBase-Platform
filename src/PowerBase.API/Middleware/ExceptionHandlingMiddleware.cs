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
            ConflictException e => (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, (object?)null),
            ConcurrencyException e => (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, (object?)null),
            UnauthorizedActionException e => (StatusCodes.Status403Forbidden, e.ErrorCode, e.Message, (object?)null),
            Domain.Exceptions.ValidationException e => (StatusCodes.Status400BadRequest, e.ErrorCode, e.Message, (object?)e.Errors),
            RecordConstraintViolationException e => (StatusCodes.Status400BadRequest, e.ErrorCode, e.Message, (object?)e.Violations),
            BadRequestException e => (StatusCodes.Status400BadRequest, e.ErrorCode, e.Message, (object?)null),
            LinkExpiredException e => (StatusCodes.Status410Gone, e.ErrorCode, e.Message, (object?)null),
            ActionGateException e => (422, e.ErrorCode, e.Message, (object?)null),
            InternalServerException e => (StatusCodes.Status500InternalServerError, e.ErrorCode, e.Message, (object?)null),
            _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred.", (object?)null)
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode;

        object body;
        if (statusCode == StatusCodes.Status500InternalServerError && _env.IsDevelopment() && exception is not InternalServerException)
        {
            // Even in development, keep the main message user-friendly so the frontend toast doesn't show raw C# errors.
            // We put the technical exception message in the 'detail' field for debugging in the network tab.
            body = new { error = new { code, message = "An unexpected error occurred on the server.", detail = exception.Message + "\n" + exception.ToString() } };
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
