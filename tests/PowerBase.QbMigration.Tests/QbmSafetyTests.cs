using FluentAssertions;
using PowerBase.Infrastructure.QbMigration;

namespace PowerBase.QbMigration.Tests;

public class QbmSafetyTests
{
    [Fact]
    public void The_powerbase_api_over_https_is_allowed()
    {
        QbmSafety.ApiUri($"https://{QbmSafety.DefaultApiHost}", null).ToString().Should().Be($"https://{QbmSafety.DefaultApiHost}/");
        QbmSafety.ApiUri("https://api.example.test/", "other.test, api.example.test").Host.Should().Be("api.example.test");
    }

    [Theory]
    [InlineData("http://powerbaseapi-wus2-fdemhzg8fycse9hx.westus2-01.azurewebsites.net")]
    [InlineData("https://evil.example.com")]
    [InlineData("https://powerbaseapi-wus2-fdemhzg8fycse9hx.westus2-01.azurewebsites.net.evil.com")]
    [InlineData("https://user@powerbaseapi-wus2-fdemhzg8fycse9hx.westus2-01.azurewebsites.net")]
    [InlineData("not a url")]
    public void Any_other_url_is_refused_before_the_token_is_sent(string url)
    {
        var act = () => QbmSafety.ApiUri(url, null);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Emails_in_report_rows_are_masked()
    {
        QbmSafety.MaskEmails("chaim@yslworkflow.com: QuickBase Super User; a@x.com too")
            .Should().Be("ch***@yslworkflow.com: QuickBase Super User; a***@x.com too");
        QbmSafety.MaskEmails("3 users, no emails here").Should().Be("3 users, no emails here");
    }

    [Theory]
    [InlineData("josé.müller@bücher.de: inactive", "jo***@bücher.de: inactive")]
    [InlineData("'o'brien!x@mail.co.uk', role", "'o***@mail.co.uk', role")]
    [InlineData("<first+tag@sub.example.org>", "<fi***@sub.example.org>")]
    public void Unusual_emails_are_masked_too(string text, string masked) =>
        QbmSafety.MaskEmails(text).Should().Be(masked);
}
