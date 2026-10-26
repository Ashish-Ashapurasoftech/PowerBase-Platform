using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Jobs;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Import;

public class AppImportJobTests
{
    [Theory]
    [InlineData(3L * 1024 * 1024, false)]
    [InlineData(3L * 1024 * 1024 + 1, true)]
    [InlineData(100, false)]
    public void Policy_OnlyFilesOverThreeMegabytesRunInBackground(long size, bool expected) =>
        AppImportJobPolicy.RunsInBackground(size).Should().Be(expected);

    [Fact]
    public void Render_ReplacesPlaceholders_EncodesHtmlAndDropsUnknownTokens()
    {
        var values = new Dictionary<string, string> { ["AppName"] = "<b>CARES</b>" };

        AppImportEmailTemplates.Render("Hi {{ AppName }} {{Nope}}!", values, encode: true)
            .Should().Be("Hi &lt;b&gt;CARES&lt;/b&gt; !");
    }

    [Fact]
    public void Render_SubjectIsPlainTextWithoutControlCharacters()
    {
        var values = new Dictionary<string, string> { ["AppName"] = "A\r\nBcc: x" };

        AppImportEmailTemplates.Render("{{AppName}}", values, encode: false).Should().Be("ABcc: x");
    }

    [Fact]
    public void ImportProgress_WithoutListener_DoesNothingAndWithListenerIsClamped()
    {
        ImportProgress.Current = null;
        ImportProgress.Report("x", 50); // no listener: must not throw

        int? seen = null;
        ImportProgress.Current = (_, p) => seen = p;
        ImportProgress.Report("x", 150);
        ImportProgress.Current = null;

        seen.Should().Be(99); // 100 is reserved for "completed"
    }

    [Fact]
    public async Task SaveTemplate_UnknownPlaceholder_IsRefused()
    {
        var sut = new EmailTemplateService(Substitute.For<IEmailTemplateRepository>(), Substitute.For<IQueryContext>());

        var act = () => sut.SaveAsync(AppImportEmailTemplates.Completed, "Done {{AppNam}}", "<p>x</p>", true, default);

        (await act.Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainKey("Placeholders");
    }

    [Fact]
    public async Task GetTemplate_WithoutOverride_ReturnsBuiltInDefault()
    {
        var repo = Substitute.For<IEmailTemplateRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((EmailTemplate?)null);
        var sut = new EmailTemplateService(repo, Substitute.For<IQueryContext>());

        var t = await sut.GetAsync(AppImportEmailTemplates.Failed, default);

        t.IsCustomized.Should().BeFalse();
        t.Subject.Should().Contain("{{FileName}}");
    }

    [Fact]
    public async Task GetTemplate_UnknownKey_IsNotFound()
    {
        var sut = new EmailTemplateService(Substitute.For<IEmailTemplateRepository>(), Substitute.For<IQueryContext>());

        await sut.Invoking(s => s.GetAsync("Nope", default)).Should().ThrowAsync<NotFoundException>();
    }
}
