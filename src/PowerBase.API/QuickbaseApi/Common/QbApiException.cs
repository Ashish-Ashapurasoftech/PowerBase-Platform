namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// Error raised by the Quickbase-compatible API. REST endpoints render it as Quickbase's
/// <c>{ "message": ..., "description": ... }</c> body with <see cref="StatusCode"/>.
/// </summary>
public sealed class QbApiException : Exception
{
    public int StatusCode { get; }
    public string Description { get; }
    /// <summary>Quickbase's "details" list for schema errors ("Multiple errors found").</summary>
    public IReadOnlyList<string>? Details { get; init; }

    public QbApiException(int statusCode, string message, string description = "") : base(message)
    {
        StatusCode = statusCode;
        Description = description;
    }

    public static QbApiException BadRequest(string description) => new(400, "Bad Request", description);
    /// <summary>Quickbase's 400 for a semantically wrong value, e.g. a merge field that is not unique.</summary>
    public static QbApiException InvalidInput(string description) => new(400, "Invalid input", description);
    /// <summary>Quickbase's 400 for an app / table / parameter it cannot use ("Invalid request").</summary>
    public static QbApiException InvalidRequest(string description) => new(400, "Invalid request", description);
    public static QbApiException Unauthorized(string description) => new(401, "Unauthorized", description);
    public static QbApiException AccessDenied(string description) => new(403, "Access denied", description);
    public static QbApiException NotFound(string description) => new(404, "Item not found", description);
}
