using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Quickbase file versions (/v1/files/{table}/{record}/{field}/{version}) read from our stored File value.</summary>
public class QbFilesTests
{
    private const string WithRevisions = """
        {"name":"c.pdf","path":"p/c","revisionNumber":3,
         "revisions":[{"name":"a.pdf","path":"p/a","revisionNumber":1},{"name":"b.pdf","path":"p/b","revisionNumber":2}]}
        """;

    [Fact]
    public void Versions_OldestFirst_CurrentLast()
    {
        QbFileService.Versions(WithRevisions).Select(v => (v.Number, v.Path, v.FileName))
            .Should().Equal((1, "p/a", "a.pdf"), (2, "p/b", "b.pdf"), (3, "p/c", "c.pdf"));
    }

    [Fact]
    public void Versions_NoRevisionNumbers_AreCounted() =>
        QbFileService.Versions("""{"name":"x.txt","path":"p/x"}""").Single().Number.Should().Be(1);

    [Fact]
    public void Versions_MultiAttachmentArray_UsesTheFirst() =>
        QbFileService.Versions("""[{"name":"x.txt","path":"p/x"},{"name":"y.txt","path":"p/y"}]""").Single().Path.Should().Be("p/x");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void Versions_Empty(string? value) => QbFileService.Versions(value).Should().BeEmpty();

    [Theory]
    [InlineData(0, "p/c")]
    [InlineData(3, "p/c")]
    [InlineData(1, "p/a")]
    public void Pick_ZeroIsCurrent(int number, string path) =>
        QbFileService.Pick(QbFileService.Versions(WithRevisions), number, 9).Path.Should().Be(path);

    [Fact]
    public void Pick_Unknown_Is404()
    {
        var act = () => QbFileService.Pick(QbFileService.Versions(WithRevisions), 7, 9);
        act.Should().Throw<QbApiException>().Which.StatusCode.Should().Be(404);
        var none = () => QbFileService.Pick([], 0, 9);
        none.Should().Throw<QbApiException>().Which.StatusCode.Should().Be(404);
    }
}
