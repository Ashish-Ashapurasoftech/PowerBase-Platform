using System.Net.Mail;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Pipelines;

public class SendEmailTests
{
    private static MailMessage Build(PipelineEmailMessage email) => (MailMessage)typeof(EmailService)
        .GetMethod("CreatePipelineMessage", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [email, "configured@example.com", "PowerBase"])!;

    [Fact]
    public void MultipleRecipientsAndQuotedDisplayNamesArePreserved()
    {
        using var message = Build(new("\"Doe, Jane\" <jane@example.com>;two@example.com\r\nthree@example.com", "Subject", "Body",
            "cc1@example.com,cc2@example.com", "bcc@example.com"));
        Assert.Equal(3, message.To.Count);
        Assert.Equal("Doe, Jane", message.To[0].DisplayName);
        Assert.Equal(2, message.CC.Count);
        Assert.Single(message.Bcc);
        Assert.Equal("configured@example.com", message.From!.Address);
    }

    [Theory]
    [InlineData("HTML", true, "High", MailPriority.High)]
    [InlineData("Plain Text", false, "Low", MailPriority.Low)]
    [InlineData("Text", false, "Normal", MailPriority.Normal)]
    public void HonorsExplicitBodyFormatAndImportance(string contentType, bool html, string importance, MailPriority priority)
    {
        using var message = Build(new("to@example.com", "Subject", "<strong>Hello</strong>", ContentType: contentType, Importance: importance));
        Assert.Equal(html, message.IsBodyHtml);
        Assert.Equal(priority, message.Priority);
        Assert.Equal("<strong>Hello</strong>", message.Body);
    }

    [Fact]
    public void HtmlModePreservesPlainTextLinesAndEscapesLiteralMarkup()
    {
        using var message = Build(new("to@example.com", "Subject", "Hi,\r\nRecord ID: 19\r\nA & B < C", ContentType: "HTML"));
        Assert.True(message.IsBodyHtml);
        Assert.Equal("Hi,<br>\r\nRecord ID: 19<br>\r\nA &amp; B &lt; C", message.Body);
    }

    [Fact]
    public void HtmlModePreservesAuthoredHtml()
    {
        using var message = Build(new("to@example.com", "Subject", "<p>Hi</p>\n<table><tr><td>19</td></tr></table>", ContentType: "HTML"));
        Assert.Equal("<p>Hi</p>\n<table><tr><td>19</td></tr></table>", message.Body);
    }

    [Theory]
    [InlineData("Invoice\r\nSeptember", "Invoice September")]
    [InlineData("  Customer\tUpdate\nReady  ", "Customer Update Ready")]
    public void DynamicMultilineSubjectIsNormalizedForSmtp(string subject, string expected)
    {
        using var message = Build(new("to@example.com", subject, "Body"));
        Assert.Equal(expected, message.Subject);
    }

    [Fact]
    public void SharedMailboxOverridesFromWithoutChangingTransportAccount()
    {
        using var message = Build(new("to@example.com", "Subject", "Body", From: "from@example.com", SharedMailbox: "shared@example.com"));
        Assert.Equal("shared@example.com", message.From!.Address);
    }

    [Theory]
    [InlineData("", "HTML", "Normal")]
    [InlineData("to@example.com", "invalid", "Normal")]
    [InlineData("to@example.com", "HTML", "invalid")]
    public void RejectsInvalidResolvedValues(string to, string contentType, string importance)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Build(new(to, "Subject", "Body", ContentType: contentType, Importance: importance)));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public async Task MissingMailServerFailsInsteadOfReportingASentMessage()
    {
        var service = new EmailService(new ConfigurationBuilder().Build(), NullLogger<EmailService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendPipelineEmailAsync(new("to@example.com", "Subject", "Body")));
    }
}
