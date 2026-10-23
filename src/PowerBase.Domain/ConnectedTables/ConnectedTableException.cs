using PowerBase.Domain.Exceptions;

namespace PowerBase.Domain.ConnectedTables;

/// <summary>An error raised by the Connected Tables module. It carries its own HTTP status so the module's exception
/// filter can answer with the right code without the global exception middleware knowing about this module.</summary>
public class ConnectedTableException : DomainException
{
    public int StatusCode { get; }

    public ConnectedTableException(string errorCode, string message, int statusCode = 400)
        : base(errorCode, message)
    {
        StatusCode = statusCode;
    }

    public static ConnectedTableException Forbidden(string code, string message) => new(code, message, 403);
    public static ConnectedTableException Conflict(string code, string message) => new(code, message, 409);
}
