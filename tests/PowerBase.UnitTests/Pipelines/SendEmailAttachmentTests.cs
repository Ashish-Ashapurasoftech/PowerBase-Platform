using System.Net;
using System.Net.Mail;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Pipelines;

public class SendEmailAttachmentTests
{
    [Fact]
    public void ResolvesUploadedAndPreviousStepAttachmentJson()
    {
        var config = new SendEmailStepConfig
        {
            UploadedAttachments = "[{\"name\":\"one.pdf\",\"path\":\"/files/one.pdf\",\"type\":\"application/pdf\"}]",
            AttachmentReferences = "first\nsecond"
        };

        var files = config.ResolveStoredAttachments(value => value switch
        {
            "first" => "{\"name\":\"two.csv\",\"path\":\"/files/two.csv\",\"type\":\"text/csv\"}",
            "second" => "[{\"name\":\"three.txt\",\"path\":\"/files/three.txt\"}]",
            _ => value ?? string.Empty
        });

        Assert.Equal(3, files.Count);
        Assert.Equal("one.pdf", files[0].Name);
        Assert.Equal("text/csv", files[1].ContentType);
        Assert.Equal("/files/three.txt", files[2].Path);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    [Theory]
    [InlineData("to", "from", "cc", "bcc")]
    [InlineData("emailTo", "emailFrom", "emailCc", "emailBcc")]
    [InlineData("toAddresses", "fromAddress", "ccAddresses", "bccAddresses")]
    public void ReadsSavedFieldNames(string to, string from, string cc, string bcc)
    {
        var config = JsonSerializer.Deserialize<SendEmailStepConfig>(JsonSerializer.Serialize(new Dictionary<string, string> {
            [to] = "to@example.com", [from] = "from@example.com", [cc] = "cc@example.com", [bcc] = "bcc@example.com"
        }), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        config.Normalize();
        Assert.Equal("to@example.com", config.ToAddresses);
        Assert.Equal("from@example.com", config.FromAddress);
        Assert.Equal("cc@example.com", config.CcAddresses);
        Assert.Equal("bcc@example.com", config.BccAddresses);
    }

    [Fact]
    public void ExplicitClearedCanonicalValueDoesNotResurrectAnOldAlias()
    {
        var config = new SendEmailStepConfig { CcAddresses = "", EmailCc = "old@example.com", To = "to@example.com", EmailBody = "Hello" };
        config.Normalize();
        Assert.Equal("", config.CcAddresses);
        Assert.Equal("Hello", config.Body);
    }

    [Fact]
    public void ResolvesMultilineAttachmentMetadataInMatchingRows()
    {
        var config = new SendEmailStepConfig { AttachmentUrl = "urls", AttachmentFileName = "one.txt\n\nthree.pdf", AttachmentMimeType = "text/plain\n\napplication/pdf" };
        var attachments = config.ResolveUrlAttachments(value => value == "urls" ? "https://files.example/one\n\nhttps://files.example/three" : value ?? "");
        Assert.Equal(2, attachments.Count);
        Assert.Equal("three.pdf", attachments[1].FileName);
        Assert.Equal("application/pdf", attachments[1].MimeType);
    }

    [Fact]
    public void RejectsAttachmentMetadataWithoutUrl()
    {
        Assert.Throws<InvalidOperationException>(() => new SendEmailStepConfig { AttachmentFileName = "lost.pdf" }.ResolveUrlAttachments(value => value ?? ""));
    }

    [Fact]
    public async Task DownloadsBytesAndHonorsFileNameAndMimeType()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("contents") }));
        using var mail = new MailMessage();
        await EmailAttachmentDownloader.AddAsync(mail, [new("https://files.example/download", "invoice.csv", "text/csv")], client, default);
        var attachment = Assert.Single(mail.Attachments);
        Assert.Equal("invoice.csv", attachment.Name);
        Assert.Equal("text/csv", attachment.ContentType.MediaType);
        using var reader = new StreamReader(attachment.ContentStream);
        Assert.Equal("contents", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task UsesResponseMetadataWhenFileNameAndMimeTypeAreBlank()
    {
        using var client = new HttpClient(new Handler(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentDisposition = new("attachment") { FileNameStar = "report.pdf" };
            response.Content.Headers.ContentType = new("application/pdf");
            return response;
        }));
        using var mail = new MailMessage();
        await EmailAttachmentDownloader.AddAsync(mail, [new("https://files.example/download")], client, default);
        Assert.Equal("report.pdf", mail.Attachments[0].Name);
        Assert.Equal("application/pdf", mail.Attachments[0].ContentType.MediaType);
    }

    [Theory]
    [InlineData("file:///C:/secrets.txt")]
    [InlineData("http://127.0.0.1/file")]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("https://user:password@files.example/file")]
    [InlineData("http://[::1]/file")]
    public async Task RejectsUnsafeUrlsBeforeDownload(string url)
    {
        using var client = new HttpClient(new Handler(_ => throw new Exception("Must not download")));
        using var mail = new MailMessage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmailAttachmentDownloader.AddAsync(mail, [new(url)], client, default));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    public void BlocksPrivateResolvedAddresses(string ip) => Assert.False(EmailAttachmentDownloader.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public async Task RejectsRedirectToPrivateHost()
    {
        using var client = new HttpClient(new Handler(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://127.0.0.1/secrets");
            return response;
        }));
        using var mail = new MailMessage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmailAttachmentDownloader.AddAsync(mail, [new("https://files.example/file")], client, default));
    }

    [Fact]
    public async Task FollowsPublicRedirects()
    {
        using var client = new HttpClient(new Handler(request => {
            if (request.RequestUri!.AbsolutePath == "/download")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("/actual", UriKind.Relative);
                return redirect;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("file") };
        }));
        using var mail = new MailMessage();
        await EmailAttachmentDownloader.AddAsync(mail, [new("https://files.example/download")], client, default);
        Assert.Single(mail.Attachments);
    }

    [Fact]
    public async Task FailedDownloadDoesNotSilentlySkipAttachment()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        using var mail = new MailMessage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmailAttachmentDownloader.AddAsync(mail, [new("https://files.example/missing")], client, default));
        Assert.Empty(mail.Attachments);
    }

    [Fact]
    public async Task EnforcesSizeLimitWithoutContentLength()
    {
        using var client = new HttpClient(new Handler(_ => {
            var content = new ByteArrayContent(new byte[checked((int)EmailAttachmentDownloader.MaxBytes + 1)]);
            content.Headers.ContentLength = null;
            return new(HttpStatusCode.OK) { Content = content };
        }));
        using var mail = new MailMessage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmailAttachmentDownloader.AddAsync(mail, [new("https://files.example/large")], client, default));
        Assert.Empty(mail.Attachments);
    }
}
