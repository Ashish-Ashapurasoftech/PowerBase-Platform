using Microsoft.AspNetCore.Mvc;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>
/// Quickbase XML API endpoint: <c>POST|GET /db/{dbid}</c> with the action in the <c>QUICKBASE-ACTION</c> header (or <c>?a=</c>).
/// Quickbase answers XML calls with HTTP 200 and reports failures in <c>errcode</c>/<c>errtext</c>; this does the same.
/// </summary>
[Route("db/{dbid}")]
public sealed class QbXmlController : ControllerBase
{
    private readonly QbXmlDispatcher _dispatcher;
    private readonly IQueryContext _identity;
    private readonly ILogger<QbXmlController> _logger;

    public QbXmlController(QbXmlDispatcher dispatcher, IQueryContext identity, ILogger<QbXmlController> logger)
    {
        _dispatcher = dispatcher;
        _identity = identity;
        _logger = logger;
    }

    [HttpPost, HttpGet]
    [Produces("text/xml")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public async Task<ContentResult> Handle(string dbid, CancellationToken ct)
    {
        string? body = null;
        if (Request.ContentLength is > 0 || Request.Headers.TransferEncoding.Count > 0)
        {
            if (Request.Body.CanSeek) Request.Body.Position = 0;
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            body = await reader.ReadToEndAsync(ct);
        }

        QbXmlRequest? req = null;
        string action = Request.Headers[QbXmlRequest.ACTION_HEADER].FirstOrDefault() ?? Request.Query["a"].FirstOrDefault() ?? "";
        try
        {
            req = QbXmlRequest.Parse(dbid, Request.Headers[QbXmlRequest.ACTION_HEADER].FirstOrDefault(), Request.Query, body);
            action = req.Action;
            if (string.IsNullOrWhiteSpace(action))
                throw new QbXmlException(QbXmlErrors.NO_SUCH_OPERATION, "No Such Operation", "This operation does not exist. Please check the URL.");
            if (_identity.UserId <= 0 || _identity.TenantId <= 0)
                throw string.IsNullOrWhiteSpace(req.Get("ticket")) || !string.IsNullOrWhiteSpace(req.Get("usertoken"))
                    ? new QbXmlException(QbXmlErrors.INVALID_USER_TOKEN, "Invalid error code: 83", "User token is invalid") // Quickbase's own text
                    : new QbXmlException(QbXmlErrors.BAD_TICKET, "Bad ticket", "Supply a valid ticket");
            var response = await _dispatcher.DispatchAsync(req, ct);
            if (response.Html is { } html)
                return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = 200 };
            if (response.Raw is { } raw) return Xml(raw, 200);
            return Xml(response.ToXml(req.Udata), 200);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var err = QbXmlErrors.From(ex);
            if (ex is not (QbXmlException or Domain.Exceptions.DomainException or Common.QbApiException or UnauthorizedAccessException))
                _logger.LogError(ex, "Quickbase XML call {Action} on {DbId} failed", action, dbid);
            var errorXml = new QbXmlResponse(action).ToXml(req?.Udata, err.Code, err.Message, err.Detail, err.Extra);
            if (QbXmlQueryActions.UsesCrlf(action)) errorXml = QbXmlQueryActions.ToCrlf(errorXml);
            return Xml(errorXml, err.Status ?? QbXmlErrors.HttpStatus(err.Code));
        }
    }

    private static ContentResult Xml(string xml, int status) => new() { Content = AsciiEntities(xml), ContentType = "text/xml; charset=utf-8", StatusCode = status };

    /// <summary>Quickbase XML writes every non-ASCII character as a decimal character reference (live realm: "Ü" → "&amp;#220;").</summary>
    public static string AsciiEntities(string xml)
    {
        if (xml.All(c => c < 128)) return xml;
        var sb = new System.Text.StringBuilder(xml.Length + 32);
        for (var i = 0; i < xml.Length; i++)
        {
            var c = xml[i];
            if (c < 128) { sb.Append(c); continue; }
            var cp = char.IsHighSurrogate(c) && i + 1 < xml.Length && char.IsLowSurrogate(xml[i + 1]) ? char.ConvertToUtf32(c, xml[++i]) : c;
            sb.Append("&#").Append(cp).Append(';');
        }
        return sb.ToString();
    }
}
