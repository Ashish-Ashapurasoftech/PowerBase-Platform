using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>Quickbase REST error body: <c>{ "message": "...", "description": "..." }</c> (developer.quickbase.com/errors).</summary>
public sealed record QbErrorBody(string Message, string Description)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Details { get; init; }

    public static ObjectResult Result(int status, string message, string description, IReadOnlyList<string>? details = null) =>
        new(new QbErrorBody(message, description) { Details = details }) { StatusCode = status };

    /// <summary>Maps any exception thrown inside a Quickbase endpoint to Quickbase's status + body.</summary>
    public static ObjectResult FromException(Exception ex) => ex switch
    {
        QbApiException e => Result(e.StatusCode, e.Message, e.Description, e.Details),
        NotFoundException e => Result(404, "Item not found", e.Message),
        UnauthorizedActionException e => Result(403, "Access denied", e.Message),
        ValidationException e => Result(400, "Bad Request", e.Message),
        RecordConstraintViolationException e => Result(400, "Bad Request", e.Message),
        BulkCreateValidationException e => Result(400, "Bad Request", e.Message),
        BadRequestException e => Result(400, "Bad Request", e.Message),
        DuplicateException e => Result(400, "Bad Request", e.Message),
        ConflictException e => Result(409, "Conflict", e.Message),
        ConcurrencyException e => Result(409, "Conflict", e.Message),
        UnauthorizedAccessException e => Result(401, "Unauthorized", e.Message),
        _ => Result(500, "Internal server error", ""),
    };
}

/// <summary>
/// Applied only to Quickbase-compatible REST controllers: renders their errors in Quickbase's format
/// before the global ExceptionHandlingMiddleware (which stays unchanged for every other endpoint) sees them.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class QbRestExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException) return;
        if (context.Exception is not (QbApiException or DomainException or UnauthorizedAccessException))
        {
            var logger = context.HttpContext.RequestServices.GetService<ILogger<QbRestExceptionFilterAttribute>>();
            logger?.LogError(context.Exception, "Quickbase-compatible API error on {Path}", context.HttpContext.Request.Path);
        }
        context.Result = QbErrorBody.FromException(context.Exception);
        context.ExceptionHandled = true;
    }
}
