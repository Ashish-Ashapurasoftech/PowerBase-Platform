using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.API.Controllers;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Pipelines.Outlook;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>Fake HTTP transport recording every request (Microsoft identity + Graph).</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string Url, string? Authorization, string Body, string? ContentRange);
    public List<Call> Calls { get; } = new();
    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.Accepted);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add(new(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body,
            request.Content?.Headers.ContentRange?.ToString()));
        return Respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object value) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    public IHttpClientFactory Factory()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(this, disposeHandler: false));
        return factory;
    }
}

public class OutlookPermissionCatalogTests
{
    [Fact]
    public void Resolve_NoOptional_ReturnsBaseAndRequiredScopes()
    {
        var scopes = OutlookPermissionCatalog.Resolve(null);
        scopes.Should().Contain(["offline_access", "openid", "User.Read", "Mail.Send", "Mail.Send.Shared", "Mail.Read", "Mail.Read.Shared", "Calendars.Read"]);
        scopes.Should().NotContain("Calendars.ReadWrite");
    }

    [Fact]
    public void Resolve_SelectedOptional_AddsOnlyThoseScopes()
    {
        var scopes = OutlookPermissionCatalog.Resolve(["calendars.readwrite"]);
        scopes.Should().Contain("Calendars.ReadWrite");
        scopes.Should().NotContain("Mail.ReadWrite.Shared");
    }

    [Fact]
    public void Resolve_UnknownScope_Throws()
    {
        var act = () => OutlookPermissionCatalog.Resolve(["Files.ReadWrite.All"]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Catalog_QuickbasePanelPlusMailReadWrite_SevenRequiredFourOptional()
    {
        OutlookPermissionCatalog.All.Count(p => p.Required).Should().Be(7);
        OutlookPermissionCatalog.All.Count(p => !p.Required).Should().Be(4);
    }

    [Fact]
    public void Resolve_AlwaysRequestsMailReadWrite_ForLargeAttachments()
    {
        OutlookPermissionCatalog.Resolve(null).Should().Contain("Mail.ReadWrite");
    }
}

public class OutlookGraphMailSenderTests
{
    private readonly RecordingHandler _handler = new();
    private readonly OutlookGraphMailSender _sender;

    public OutlookGraphMailSenderTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Microsoft:GraphBaseUrl"] = "https://graph.test/v1.0" }).Build();
        _sender = new OutlookGraphMailSender(_handler.Factory(), config, NullLogger<OutlookGraphMailSender>.Instance);
    }

    private static PipelineEmailMessage Message(string? shared = null, string? from = null, string saveToSent = "Yes",
        IReadOnlyList<PipelineEmailContentAttachment>? attachments = null) =>
        new("one@example.com, \"Doe, Jane\" <jane@example.com>", "Invoice\r\nReady", "<p>Hello</p>", "cc@example.com", "bcc@example.com",
            from, shared, "HTML", "High", saveToSent, null, null, attachments);

    [Fact]
    public async Task SendAsync_ConnectedAccount_PostsGraphSendMailWithAllFields()
    {
        await _sender.SendAsync("token-1", Message(saveToSent: "No"), CancellationToken.None);

        var call = _handler.Calls.Should().ContainSingle().Subject;
        call.Url.Should().Be("https://graph.test/v1.0/me/sendMail");
        call.Authorization.Should().Be("Bearer token-1");
        var json = JsonNode.Parse(call.Body)!;
        json["saveToSentItems"]!.GetValue<bool>().Should().BeFalse();
        var message = json["message"]!;
        message["subject"]!.GetValue<string>().Should().Be("Invoice Ready");
        message["body"]!["contentType"]!.GetValue<string>().Should().Be("HTML");
        message["importance"]!.GetValue<string>().Should().Be("high");
        message["toRecipients"]!.AsArray().Select(r => r!["emailAddress"]!["address"]!.GetValue<string>())
            .Should().Equal("one@example.com", "jane@example.com");
        message["toRecipients"]![1]!["emailAddress"]!["name"]!.GetValue<string>().Should().Be("Doe, Jane");
        message["ccRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>().Should().Be("cc@example.com");
        message["bccRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>().Should().Be("bcc@example.com");
        message["from"].Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_SharedMailbox_SendsFromSharedMailboxEndpoint()
    {
        await _sender.SendAsync("token-1", Message(shared: "support@example.com"), CancellationToken.None);
        _handler.Calls.Single().Url.Should().Be("https://graph.test/v1.0/users/support%40example.com/sendMail");
        JsonNode.Parse(_handler.Calls.Single().Body)!["saveToSentItems"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_FromAddress_SetsGraphFrom()
    {
        await _sender.SendAsync("token-1", Message(from: "sales@example.com"), CancellationToken.None);
        var message = JsonNode.Parse(_handler.Calls.Single().Body)!["message"]!;
        message["from"]!["emailAddress"]!["address"]!.GetValue<string>().Should().Be("sales@example.com");
        _handler.Calls.Single().Url.Should().EndWith("/me/sendMail");
    }

    [Fact]
    public async Task SendAsync_InvalidSharedMailbox_ThrowsBeforeCallingGraph()
    {
        var act = () => _sender.SendAsync("token-1", Message(shared: "not an address"), CancellationToken.None);
        await act.Should().ThrowAsync<FormatException>();
        _handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_SmallAttachment_IsInlinedAsFileAttachment()
    {
        var attachment = new PipelineEmailContentAttachment("a.txt", "text/plain", Encoding.UTF8.GetBytes("hello"));
        await _sender.SendAsync("token-1", Message(attachments: [attachment]), CancellationToken.None);
        var item = JsonNode.Parse(_handler.Calls.Single().Body)!["message"]!["attachments"]![0]!;
        item["@odata.type"]!.GetValue<string>().Should().Be("#microsoft.graph.fileAttachment");
        item["name"]!.GetValue<string>().Should().Be("a.txt");
        item["contentBytes"]!.GetValue<string>().Should().Be(Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")));
    }

    [Fact]
    public async Task SendAsync_LargeAttachment_UsesDraftUploadSessionAndSend()
    {
        _handler.Respond = (request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/me/messages")) return RecordingHandler.Json(HttpStatusCode.Created, new { id = "draft-1" });
            if (url.EndsWith("/createUploadSession")) return RecordingHandler.Json(HttpStatusCode.Created, new { uploadUrl = "https://upload.test/session" });
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var big = new byte[OutlookGraphMailSender.UPLOAD_CHUNK_BYTES + 1000];
        await _sender.SendAsync("token-1", Message(attachments: [new("big.bin", "application/octet-stream", big)]), CancellationToken.None);

        _handler.Calls.Select(c => c.Url).Should().Equal(
            "https://graph.test/v1.0/me/messages",
            "https://graph.test/v1.0/me/messages/draft-1/attachments/createUploadSession",
            "https://upload.test/session",
            "https://upload.test/session",
            "https://graph.test/v1.0/me/messages/draft-1/send");
        var uploads = _handler.Calls.Where(c => c.Url.StartsWith("https://upload.test")).ToList();
        uploads.Should().OnlyContain(c => c.Authorization == null, "the pre-authorized upload URL must not receive the bearer token");
        uploads[0].ContentRange.Should().Be($"bytes 0-{OutlookGraphMailSender.UPLOAD_CHUNK_BYTES - 1}/{big.Length}");
        uploads[1].ContentRange.Should().Be($"bytes {OutlookGraphMailSender.UPLOAD_CHUNK_BYTES}-{big.Length - 1}/{big.Length}");
    }

    [Fact]
    public async Task SendAsync_SendAsDenied_ExplainsMailboxPermissions()
    {
        _handler.Respond = (_, _) => RecordingHandler.Json(HttpStatusCode.Forbidden, new { error = new { code = "ErrorSendAsDenied", message = "denied" } });
        var act = () => _sender.SendAsync("token-1", Message(shared: "support@example.com"), CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Send As or Send on Behalf");
    }

    [Fact]
    public async Task SendAsync_LargeAttachmentWithoutMailReadWrite_ExplainsReconnect()
    {
        // Regression 2026-11-19: draft creation returned ErrorAccessDenied and the step blamed shared-mailbox permissions.
        _handler.Respond = (_, _) => RecordingHandler.Json(HttpStatusCode.Forbidden, new { error = new { code = "ErrorAccessDenied", message = "Access is denied. Check credentials and try again." } });
        var big = new byte[OutlookGraphMailSender.INLINE_ATTACHMENT_LIMIT + 1];
        var act = () => _sender.SendAsync("token-1", Message(attachments: [new("big.pdf", "application/pdf", big)]), CancellationToken.None);
        var error = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        error.Message.Should().Contain("Mail.ReadWrite").And.Contain("Reconnect").And.NotContain("Send As");
        _handler.Calls.Should().ContainSingle().Which.Url.Should().EndWith("/me/messages");
    }

    [Fact]
    public async Task SendAsync_AccessDeniedOnOwnMailbox_DoesNotBlameSharedMailbox()
    {
        _handler.Respond = (_, _) => RecordingHandler.Json(HttpStatusCode.Forbidden, new { error = new { code = "ErrorAccessDenied", message = "denied" } });
        var act = () => _sender.SendAsync("token-1", Message(), CancellationToken.None);
        var error = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        error.Message.Should().Contain("Reconnect").And.NotContain("Send As");
    }

    [Fact]
    public async Task SendAsync_50MbAttachment_UploadsInChunksAndSends()
    {
        _handler.Respond = (request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/me/messages")) return RecordingHandler.Json(HttpStatusCode.Created, new { id = "draft-50" });
            if (url.EndsWith("/createUploadSession")) return RecordingHandler.Json(HttpStatusCode.Created, new { uploadUrl = "https://upload.test/s50" });
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var file = new byte[50 * 1024 * 1024];
        await _sender.SendAsync("token-1", Message(attachments: [new("big-50mb.zip", "application/zip", file)]), CancellationToken.None);

        var uploads = _handler.Calls.Where(c => c.Url.StartsWith("https://upload.test")).ToList();
        uploads.Should().HaveCount((int)Math.Ceiling(file.Length / (double)OutlookGraphMailSender.UPLOAD_CHUNK_BYTES));
        uploads.Last().ContentRange.Should().EndWith($"-{file.Length - 1}/{file.Length}");
        JsonNode.Parse(_handler.Calls.Single(c => c.Url.EndsWith("/createUploadSession")).Body)!["AttachmentItem"]!["size"]!.GetValue<int>().Should().Be(file.Length);
        _handler.Calls.Last().Url.Should().EndWith("/messages/draft-50/send");
    }

    [Fact]
    public async Task SendAsync_Over150Mb_RejectedBeforeCallingGraph()
    {
        var file = new byte[PipelineEmailLimits.OUTLOOK_MAX_ATTACHMENT_BYTES + 1];
        var act = () => _sender.SendAsync("token-1", Message(attachments: [new("huge.bin", "application/octet-stream", file)]), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*150 MB*");
        _handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_MailboxSizeLimitExceeded_ExplainsMaxSendSize()
    {
        _handler.Respond = (request, _) => request.RequestUri!.ToString().EndsWith("/me/messages")
            ? RecordingHandler.Json(HttpStatusCode.Created, new { id = "d" })
            : RecordingHandler.Json(HttpStatusCode.BadRequest, new { error = new { code = "ErrorMessageSizeExceeded", message = "The message exceeds the maximum supported size." } });
        var file = new byte[40 * 1024 * 1024];
        var act = () => _sender.SendAsync("token-1", Message(attachments: [new("40mb.bin", "application/octet-stream", file)]), CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("MaxSendSize");
    }

    [Fact]
    public void SmtpPath_StillLimitedTo25Mb()
    {
        var file = new byte[PipelineEmailLimits.SMTP_MAX_ATTACHMENT_BYTES + 1];
        var act = () => EmailService.CreatePipelineMessage(Message(attachments: [new("26mb.bin", "application/octet-stream", file)]), "noreply@example.com", "PowerBase");
        act.Should().Throw<InvalidOperationException>().WithMessage("*25 MB*");
    }

    [Fact]
    public async Task SendAsync_SmallAttachment_UsesSendMailOnly_NoDraftNeeded()
    {
        var file = new byte[OutlookGraphMailSender.INLINE_ATTACHMENT_LIMIT - 10];
        await _sender.SendAsync("token-1", Message(attachments: [new("ok.pdf", "application/pdf", file)]), CancellationToken.None);
        _handler.Calls.Should().ContainSingle().Which.Url.Should().EndWith("/me/sendMail");
    }

    [Fact]
    public async Task SendAsync_InvalidImportance_RejectedByShared_Validation()
    {
        var act = () => _sender.SendAsync("token-1", Message() with { Importance = "Urgent" }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*importance*");
        _handler.Calls.Should().BeEmpty();
    }
}

public class OutlookConnectionServiceTests
{
    private const long OWNER = 7;
    private readonly RecordingHandler _handler = new();
    private readonly IMicrosoftConnectionRepository _repository = Substitute.For<IMicrosoftConnectionRepository>();
    private readonly IOutlookGraphMailSender _sender = Substitute.For<IOutlookGraphMailSender>();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private OutlookConnectionService _service;

    public OutlookConnectionServiceTests() => _service = Create(configured: true);

    private OutlookConnectionService Create(bool configured)
    {
        var values = new Dictionary<string, string?>
        {
            ["Microsoft:AuthorityUrl"] = "https://login.test/common/oauth2/v2.0",
            ["Microsoft:GraphBaseUrl"] = "https://graph.test/v1.0"
        };
        if (configured) { values["Microsoft:ClientId"] = "client-1"; values["Microsoft:ClientSecret"] = "secret-1"; }
        return new OutlookConnectionService(_repository, _sender, _handler.Factory(), new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            _protection, NullLogger<OutlookConnectionService>.Instance);
    }

    private MicrosoftConnection Row(DateTimeOffset? expires, string? accessToken = "access-old") => new()
    {
        Id = 1, PublicId = Guid.NewGuid(), CreatedByUserId = OWNER, AccountEmail = "kuldip@example.com", DisplayName = "kuldip",
        EncryptedRefreshToken = _service.Protect("refresh-old"),
        EncryptedAccessToken = accessToken == null ? null : _service.Protect(accessToken),
        AccessTokenExpiresAt = expires, Status = MicrosoftConnectionStatuses.Connected
    };

    [Fact]
    public async Task GetAccessToken_ValidCachedToken_DoesNotCallMicrosoft()
    {
        var row = Row(DateTimeOffset.UtcNow.AddMinutes(30));
        _repository.GetByPublicIdForUserAsync(row.PublicId, OWNER, Arg.Any<CancellationToken>()).Returns(row);
        (await _service.GetAccessTokenAsync(row.PublicId, OWNER, CancellationToken.None)).Should().Be("access-old");
        _handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAccessToken_Expired_RefreshesAndStoresRotatedRefreshToken()
    {
        var row = Row(DateTimeOffset.UtcNow.AddMinutes(-1));
        _repository.GetByPublicIdForUserAsync(row.PublicId, OWNER, Arg.Any<CancellationToken>()).Returns(row);
        _repository.UpdateAsync(Arg.Any<MicrosoftConnection>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<MicrosoftConnection>());
        _handler.Respond = (_, _) => RecordingHandler.Json(HttpStatusCode.OK, new { access_token = "access-new", refresh_token = "refresh-new", expires_in = 3600 });

        (await _service.GetAccessTokenAsync(row.PublicId, OWNER, CancellationToken.None)).Should().Be("access-new");

        var call = _handler.Calls.Single();
        call.Url.Should().Be("https://login.test/common/oauth2/v2.0/token");
        call.Body.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=refresh-old").And.Contain("client_id=client-1");
        await _repository.Received(1).UpdateAsync(Arg.Is<MicrosoftConnection>(c =>
            _service.Unprotect(c.EncryptedRefreshToken) == "refresh-new" && _service.Unprotect(c.EncryptedAccessToken!) == "access-new"
            && c.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessToken_RevokedGrant_MarksReconnectRequired()
    {
        var row = Row(null, accessToken: null);
        _repository.GetByPublicIdForUserAsync(row.PublicId, OWNER, Arg.Any<CancellationToken>()).Returns(row);
        _repository.UpdateAsync(Arg.Any<MicrosoftConnection>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<MicrosoftConnection>());
        _handler.Respond = (_, _) => RecordingHandler.Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "AADSTS70000: token revoked" });

        var act = () => _service.GetAccessTokenAsync(row.PublicId, OWNER, CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("reconnected");
        await _repository.Received(1).UpdateAsync(Arg.Is<MicrosoftConnection>(c => c.Status == MicrosoftConnectionStatuses.ReconnectRequired), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessToken_ReconnectRequired_FailsWithoutCallingMicrosoft()
    {
        var row = Row(DateTimeOffset.UtcNow.AddMinutes(30));
        row.Status = MicrosoftConnectionStatuses.ReconnectRequired;
        _repository.GetByPublicIdForUserAsync(row.PublicId, OWNER, Arg.Any<CancellationToken>()).Returns(row);
        await FluentActions.Invoking(() => _service.GetAccessTokenAsync(row.PublicId, OWNER, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        _handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAccessToken_AccountOfAnotherUser_IsUnauthorized()
    {
        var id = Guid.NewGuid();
        _repository.GetByPublicIdForUserAsync(id, OWNER, Arg.Any<CancellationToken>()).Returns((MicrosoftConnection?)null);
        await FluentActions.Invoking(() => _service.GetAccessTokenAsync(id, OWNER, CancellationToken.None)).Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SendAsync_UsesResolvedTokenForGraph()
    {
        var row = Row(DateTimeOffset.UtcNow.AddMinutes(30));
        _repository.GetByPublicIdForUserAsync(row.PublicId, OWNER, Arg.Any<CancellationToken>()).Returns(row);
        var message = new PipelineEmailMessage("a@example.com", "s", "b");
        await _service.SendAsync(row.PublicId, OWNER, message, CancellationToken.None);
        await _sender.Received(1).SendAsync("access-old", message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upsert_SameMicrosoftAccount_UpdatesExistingRow()
    {
        var existing = Row(null);
        _repository.GetByMicrosoftUserAsync(OWNER, MicrosoftConnectionProviders.Outlook, "ms-1", Arg.Any<CancellationToken>()).Returns(existing);
        _repository.UpdateAsync(Arg.Any<MicrosoftConnection>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<MicrosoftConnection>());
        existing.Status = MicrosoftConnectionStatuses.ReconnectRequired;

        await _service.UpsertAsync(OWNER, new("ms-1", "Kuldip T", "kuldip@example.com"),
            new("a", "r", DateTimeOffset.UtcNow.AddHours(1), "Mail.Send"), CancellationToken.None);

        await _repository.DidNotReceive().CreateAsync(Arg.Any<MicrosoftConnection>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpdateAsync(Arg.Is<MicrosoftConnection>(c => c.Status == MicrosoftConnectionStatuses.Connected
            && c.DisplayName == "Kuldip T" && _service.Unprotect(c.EncryptedRefreshToken) == "r"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upsert_NewAccount_CreatesEncryptedRow()
    {
        _repository.CreateAsync(Arg.Any<MicrosoftConnection>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<MicrosoftConnection>());
        var row = await _service.UpsertAsync(OWNER, new("ms-2", "Ops", "ops@example.com"), new("a", "r", DateTimeOffset.UtcNow.AddHours(1), "Mail.Send"), CancellationToken.None);
        row.EncryptedRefreshToken.Should().NotBe("r");
        row.CreatedByUserId.Should().Be(OWNER);
        row.MicrosoftUserId.Should().Be("ms-2");
    }

    [Fact]
    public async Task Upsert_NoRefreshToken_Throws()
    {
        await FluentActions.Invoking(() => _service.UpsertAsync(OWNER, new("ms", "x", "x@example.com"), new("a", null, DateTimeOffset.UtcNow, null), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*refresh token*");
    }

    [Fact]
    public async Task RedeemCode_SendsPkceVerifierAndSecret()
    {
        _handler.Respond = (_, _) => RecordingHandler.Json(HttpStatusCode.OK, new { access_token = "a", refresh_token = "r", expires_in = 10, scope = "Mail.Send" });
        var token = await _service.RedeemCodeAsync("code-1", "verifier-1", "https://api.test/cb", ["Mail.Send"], CancellationToken.None);
        token.RefreshToken.Should().Be("r");
        _handler.Calls.Single().Body.Should().Contain("code_verifier=verifier-1").And.Contain("client_secret=secret-1").And.Contain("grant_type=authorization_code");
    }

    [Fact]
    public async Task NotConfigured_ReportsConfigurationError()
    {
        _service = Create(configured: false);
        _service.IsConfigured.Should().BeFalse();
        await FluentActions.Invoking(() => _service.RedeemCodeAsync("c", "v", "r", [], CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*not configured*");
    }

    [Fact]
    public async Task GetProfile_FallsBackToUserPrincipalName()
    {
        _handler.Respond = (_, _) => RecordingHandler.Json(HttpStatusCode.OK, new { id = "ms-1", displayName = "Kuldip", userPrincipalName = "kuldip@contoso.com" });
        var profile = await _service.GetProfileAsync("a", CancellationToken.None);
        profile.Should().Be(new OutlookConnectionService.Profile("ms-1", "Kuldip", "kuldip@contoso.com"));
    }
}

public class PipelineOutlookConnectionsControllerTests
{
    private const long USER = 7;
    private readonly RecordingHandler _handler = new();
    private readonly IMicrosoftConnectionRepository _repository = Substitute.For<IMicrosoftConnectionRepository>();
    private readonly IQueryContext _identity = Substitute.For<IQueryContext>();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly PipelineOutlookConnectionsController _controller;

    public PipelineOutlookConnectionsControllerTests()
    {
        _identity.UserId.Returns(USER);
        _identity.TenantId.Returns(40L);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Microsoft:ClientId"] = "client-1", ["Microsoft:ClientSecret"] = "secret-1",
            ["Microsoft:AuthorityUrl"] = "https://login.test/common/oauth2/v2.0", ["Microsoft:GraphBaseUrl"] = "https://graph.test/v1.0"
        }).Build();
        var service = new OutlookConnectionService(_repository, Substitute.For<IOutlookGraphMailSender>(), _handler.Factory(), config, _protection,
            NullLogger<OutlookConnectionService>.Instance);
        _controller = new PipelineOutlookConnectionsController(_repository, service, _identity, config, _protection,
            NullLogger<PipelineOutlookConnectionsController>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("api.test");
        _controller.ControllerContext = new ControllerContext { HttpContext = http };
    }

    private string AuthorizeAndGetState(List<string>? optional = null)
    {
        var result = _controller.Authorize(new(optional)).Should().BeOfType<OkObjectResult>().Subject;
        var url = (string)result.Value!.GetType().GetProperty("authorizationUrl")!.GetValue(result.Value)!;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query);
        return query["state"]!;
    }

    [Fact]
    public void Authorize_BuildsCommonAuthorityUrlWithPkceAndScopes()
    {
        var result = _controller.Authorize(new(["Calendars.ReadWrite"])).Should().BeOfType<OkObjectResult>().Subject;
        var url = (string)result.Value!.GetType().GetProperty("authorizationUrl")!.GetValue(result.Value)!;
        url.Should().StartWith("https://login.test/common/oauth2/v2.0/authorize?");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query);
        query["client_id"].ToString().Should().Be("client-1");
        query["code_challenge_method"].ToString().Should().Be("S256");
        query["redirect_uri"].ToString().Should().Be("https://api.test/pipelines/outlook-connections/oauth/callback");
        query["scope"].ToString().Should().Contain("offline_access").And.Contain("Mail.Send.Shared").And.Contain("Calendars.ReadWrite");
        url.Should().NotContain("secret-1");
    }

    [Fact]
    public void Authorize_UnknownPermission_ReturnsBadRequest()
    {
        _controller.Authorize(new(["Files.ReadWrite.All"])).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Callback_TamperedState_ReturnsErrorPage()
    {
        var page = (ContentResult)await _controller.Callback("tampered", "code", null, null, CancellationToken.None);
        page.StatusCode.Should().Be(400);
        page.Content.Should().Contain("Invalid sign-in state");
        _handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_UserCancelled_ReturnsErrorPage()
    {
        var state = AuthorizeAndGetState();
        var page = (ContentResult)await _controller.Callback(state, null, "access_denied", "The user cancelled.", CancellationToken.None);
        page.StatusCode.Should().Be(400);
        page.Content.Should().Contain("cancelled or denied");
    }

    [Fact]
    public async Task Callback_Success_StoresAccountAndPostsMessageToOpener()
    {
        var state = AuthorizeAndGetState();
        _handler.Respond = (request, _) => request.RequestUri!.ToString().EndsWith("/token")
            ? RecordingHandler.Json(HttpStatusCode.OK, new { access_token = "a", refresh_token = "r", expires_in = 3600, scope = "Mail.Send" })
            : RecordingHandler.Json(HttpStatusCode.OK, new { id = "ms-1", displayName = "kuldip thakkar", mail = "kuldip@example.com" });
        var created = new MicrosoftConnection { PublicId = Guid.NewGuid(), DisplayName = "kuldip thakkar", AccountEmail = "kuldip@example.com" };
        _repository.CreateAsync(Arg.Any<MicrosoftConnection>(), Arg.Any<CancellationToken>()).Returns(created);

        var page = (ContentResult)await _controller.Callback(state, "code-1", null, null, CancellationToken.None);

        page.StatusCode.Should().Be(200);
        page.Content.Should().Contain("powerbase-outlook").And.Contain(created.PublicId.ToString()).And.NotContain("\"r\"");
        _identity.Received().SetTenantId(40L);
        _handler.Calls[0].Body.Should().Contain("code=code-1").And.Contain("code_verifier=");
        await _repository.Received(1).CreateAsync(Arg.Is<MicrosoftConnection>(c => c.CreatedByUserId == USER && c.MicrosoftUserId == "ms-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callback_HtmlInMessage_IsEscaped()
    {
        var state = AuthorizeAndGetState();
        var page = (ContentResult)await _controller.Callback(state, null, "x", "<script>alert(1)</script>", CancellationToken.None);
        page.Content.Should().NotContain("<script>alert(1)</script>");
    }

    [Fact]
    public async Task List_ReturnsOnlyPublicFieldsOfCurrentUserAccounts()
    {
        _repository.ListForUserAsync(USER, MicrosoftConnectionProviders.Outlook, Arg.Any<CancellationToken>())
            .Returns([new MicrosoftConnection { Id = 99, PublicId = Guid.NewGuid(), DisplayName = "k", AccountEmail = "k@example.com", EncryptedRefreshToken = "secret", Status = "connected" }]);
        var result = (await _controller.List(CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(result.Value);
        json.Should().Contain("k@example.com").And.NotContain("secret").And.NotContain("\"Id\":99").And.NotContain("\"id\":99");
    }

    [Fact]
    public async Task Delete_AccountOfAnotherUser_ReturnsNotFound()
    {
        _repository.GetByPublicIdForUserAsync(Arg.Any<Guid>(), USER, Arg.Any<CancellationToken>()).Returns((MicrosoftConnection?)null);
        (await _controller.Delete(Guid.NewGuid(), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        await _repository.DidNotReceive().SoftDeleteAsync(Arg.Any<MicrosoftConnection>(), Arg.Any<CancellationToken>());
    }
}

/// <summary>Engine routing: Outlook account → Graph; everything else keeps the SMTP path (regression).</summary>
public class OutlookEmailEngineRoutingTests
{
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IOutlookConnectionService _outlook = Substitute.For<IOutlookConnectionService>();
    private readonly PipelineEngine _engine;

    public OutlookEmailEngineRoutingTests()
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IOutlookConnectionService)).Returns(_outlook);
        var recordWrite = Substitute.For<IRecordWriteService>();
        _engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), recordWrite,
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(),
            _emailService, Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(),
            Substitute.For<IQueryContext>(), Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), provider,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    private async Task<string> RunAsync(string subtype, object config, long owner = 55)
    {
        var step = new PipelineStep { Id = 2, RefId = "b", Type = "email", Subtype = subtype, ConfigJson = JsonSerializer.Serialize(config) };
        var payload = JsonSerializer.Serialize(new { steps = new { a = new { email = "one@example.com", shared = "support@example.com" } } });
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var context = new Dictionary<string, object> { ["_CreatedBy"] = owner };
        return await (Task<string>)method.Invoke(_engine, [step, payload, context, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "trigger_1", CancellationToken.None])!;
    }

    [Fact]
    public async Task OutlookStepWithAccount_SendsThroughOutlookAsPipelineOwner()
    {
        var id = Guid.NewGuid();
        var output = await RunAsync("send-email-outlook", new
        {
            outlookConnectionId = id.ToString(), toAddresses = "{{steps.a.email}}", subject = "Hi", body = "Body",
            sharedMailbox = "{{steps.a.shared}}", fromAddress = "sales@example.com", saveToSentItems = "No", importance = "Low"
        });

        await _outlook.Received(1).SendAsync(id, 55, Arg.Is<PipelineEmailMessage>(m => m.To == "one@example.com" && m.SharedMailbox == "support@example.com"
            && m.From == "sales@example.com" && m.SaveToSentItems == "No" && m.Importance == "Low" && m.RequireContent), Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendPipelineEmailAsync(Arg.Any<PipelineEmailMessage>(), Arg.Any<CancellationToken>());
        output.Should().Contain("Microsoft Outlook");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData(null)]
    public async Task OutlookStepWithoutValidAccount_FallsBackToSmtp(string? connectionId)
    {
        await RunAsync("send-email-outlook", new { outlookConnectionId = connectionId, toAddresses = "one@example.com", subject = "Hi", body = "Body" });
        await _emailService.Received(1).SendPipelineEmailAsync(Arg.Any<PipelineEmailMessage>(), Arg.Any<CancellationToken>());
        await _outlook.DidNotReceive().SendAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<PipelineEmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlainSendEmailStep_IgnoresOutlookAccountAndUsesSmtp()
    {
        await RunAsync("send-email", new { outlookConnectionId = Guid.NewGuid().ToString(), toAddresses = "one@example.com", subject = "Hi", body = "Body" });
        await _emailService.Received(1).SendPipelineEmailAsync(Arg.Any<PipelineEmailMessage>(), Arg.Any<CancellationToken>());
        await _outlook.DidNotReceive().SendAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<PipelineEmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OutlookFailure_PropagatesSoStepRunFails()
    {
        _outlook.SendAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<PipelineEmailMessage>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("The Microsoft Outlook account x must be reconnected."));
        var act = () => RunAsync("send-email-outlook", new { outlookConnectionId = Guid.NewGuid().ToString(), toAddresses = "one@example.com", subject = "Hi", body = "Body" });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reconnected*");
        await _emailService.DidNotReceive().SendPipelineEmailAsync(Arg.Any<PipelineEmailMessage>(), Arg.Any<CancellationToken>());
    }
}
