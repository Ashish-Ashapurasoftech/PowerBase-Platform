using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>Quickbase XML error codes (help.quickbase.com/docs/error-codes).</summary>
public static class QbXmlErrors
{
    public const int NO_ERROR = 0;
    public const int INVALID_INPUT = 2;
    public const int INSUFFICIENT_PERMISSIONS = 3;
    public const int BAD_TICKET = 4;
    public const int INVALID_CHOICE = 9;
    public const int XML_PARSE = 11;
    public const int BAD_DBID = 14;
    public const int UNKNOWN_USER = 21;
    public const int REGISTRATION_REQUIRED = 27;
    public const int NO_SUCH_RECORD = 30;
    public const int NO_SUCH_FIELD = 31;
    public const int CANNOT_CHANGE_FIELD = 34;
    public const int MISSING_REQUIRED_FIELD = 50;
    public const int NOT_UNIQUE = 51;
    public const int MISSING_IMPORT_FIELDS = 53;
    public const int UPDATE_CONFLICT = 60;
    public const int ACCESS_DENIED = 83;
    public const int INVALID_ROLE = 110;
    public const int USER_EXISTS = 111;
    public const int USER_ALREADY_IN_ROLE = 113;
    public const int SYNTAX_ERROR = 6;
    public const int NO_SUCH_OPERATION = 5;
    public const int INVALID_USER_TOKEN = 83;

    /// <summary>HTTP status Quickbase sends with an XML error (measured on a live realm): 404 for a missing record / field /
    /// operation, 401 for an unknown user, 400 for every other error, 200 when there is none.</summary>
    /// <summary>Quickbase's answer for a dbid it cannot find (live realm: errcode 83, "User token is invalid").</summary>
    public static QbXmlException UnknownDbid() => new(INVALID_USER_TOKEN, "Invalid error code: 83", "User token is invalid");

    public static int HttpStatus(int errcode) => errcode switch
    {
        NO_ERROR => 200,
        NO_SUCH_RECORD or NO_SUCH_FIELD or NO_SUCH_OPERATION => 404,
        UNKNOWN_USER => 401,
        _ => 400,
    };

    /// <summary>Maps any exception raised while serving an XML call to an errcode/errtext/errdetail triple.</summary>
    public static QbXmlException From(Exception ex) => ex switch
    {
        QbXmlException x => x,
        QbApiException { StatusCode: 401 } q => new(BAD_TICKET, "Bad ticket", q.Description),
        QbApiException { StatusCode: 403 } q => new(INSUFFICIENT_PERMISSIONS, "Insufficient permissions", q.Description),
        QbApiException { StatusCode: 404 } q => new(NO_SUCH_RECORD, "No such record", q.Description),
        QbApiException q => new(INVALID_INPUT, "Invalid input", q.Description),
        NotFoundException e => new(e.ErrorCode.StartsWith("FIELD", StringComparison.Ordinal) ? NO_SUCH_FIELD
            : e.ErrorCode.StartsWith("TABLE", StringComparison.Ordinal) || e.ErrorCode.StartsWith("APP", StringComparison.Ordinal) ? BAD_DBID
            : NO_SUCH_RECORD, e.ErrorCode.StartsWith("FIELD", StringComparison.Ordinal) ? "No such field"
            : e.ErrorCode.StartsWith("TABLE", StringComparison.Ordinal) || e.ErrorCode.StartsWith("APP", StringComparison.Ordinal) ? "Missing DBID or DBID of wrong type"
            : "No such record", e.Message),
        UnauthorizedActionException e => new(INSUFFICIENT_PERMISSIONS, "Insufficient permissions", e.Message),
        UnauthorizedAccessException e => new(ACCESS_DENIED, "Access denied", e.Message),
        RecordConstraintViolationException e => new(e.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ? NOT_UNIQUE : MISSING_REQUIRED_FIELD,
            e.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ? "Attempting to add a non-unique value to a field marked unique" : "Missing required field", e.Message),
        ConcurrencyException e => new(UPDATE_CONFLICT, "Update conflict detected", e.Message),
        DomainException e => new(INVALID_INPUT, "Invalid input", e.Message),
        _ => new(INVALID_INPUT, "Invalid input", ""),
    };
}

public sealed class QbXmlException : Exception
{
    public int Code { get; }
    public string Detail { get; }
    /// <summary>Elements Quickbase still returns with this error (API_EditRecord update conflict returns the &lt;rid&gt;).</summary>
    public IReadOnlyList<System.Xml.Linq.XElement> Extra { get; init; } = Array.Empty<System.Xml.Linq.XElement>();

    public QbXmlException(int code, string text, string detail = "") : base(text)
    {
        Code = code;
        Detail = detail;
    }
}
