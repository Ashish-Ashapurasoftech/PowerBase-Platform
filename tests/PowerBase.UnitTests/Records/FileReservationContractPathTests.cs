using System.Text.Json.Nodes;
using FluentAssertions;
using PowerBase.Application.Records;

namespace PowerBase.UnitTests.Records;

/// <summary>AllPaths / ReplacePaths, used by Copy App with data to give the copy its own files.</summary>
public class FileReservationContractPathTests
{
    private const string WithRevisions = """
        {"name":"v3.pdf","path":"/files/c.pdf","size":30,"revisionId":"r3","revisionNumber":3,"revisionSequence":3,
         "reservation":{"userId":7,"userName":"Asha","reservedOn":"2026-11-01T10:00:00Z"},
         "revisions":[{"name":"v1.pdf","path":"/files/a.pdf","revisionId":"r1","revisionNumber":1},
                      {"name":"v2.pdf","path":"/files/b.pdf","revisionId":"r2","revisionNumber":2}]}
        """;

    private static readonly Func<string, string?> ToCopy = path => path.Replace("/files/", "/files/copy-");

    [Fact]
    public void AllPaths_OneAttachment_ReturnsCurrentFileAndEveryRevision()
    {
        FileReservationContract.AllPaths(WithRevisions)
            .Should().BeEquivalentTo(["/files/c.pdf", "/files/a.pdf", "/files/b.pdf"]);
    }

    [Fact]
    public void AllPaths_ArrayOfAttachments_ReturnsThePathsOfEveryAttachment()
    {
        const string value = """[{"name":"x.png","path":"/files/x.png"},{"name":"y.png","path":"/files/y.png"}]""";

        FileReservationContract.AllPaths(value).Should().BeEquivalentTo(["/files/x.png", "/files/y.png"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("42")]
    public void AllPaths_NoFileValue_ReturnsNothing(string? value)
    {
        FileReservationContract.AllPaths(value).Should().BeEmpty();
    }

    [Fact]
    public void ReplacePaths_EveryFileFound_ReplacesEveryPathAndKeepsTheOtherKeys()
    {
        var result = FileReservationContract.ReplacePaths(WithRevisions, ToCopy);

        result.MissingFiles.Should().BeEmpty();
        var value = JsonNode.Parse(result.Value!)!.AsObject();
        value["path"]!.GetValue<string>().Should().Be("/files/copy-c.pdf");
        value["revisions"]!.AsArray().Select(r => r!["path"]!.GetValue<string>())
            .Should().Equal("/files/copy-a.pdf", "/files/copy-b.pdf");
        value["name"]!.GetValue<string>().Should().Be("v3.pdf");
        value["size"]!.GetValue<int>().Should().Be(30);
        value["revisionSequence"]!.GetValue<int>().Should().Be(3);
        value["revisions"]![1]!["revisionId"]!.GetValue<string>().Should().Be("r2");
        value["reservation"]!["userName"]!.GetValue<string>().Should().Be("Asha");
    }

    [Fact]
    public void ReplacePaths_OlderRevisionMissing_LeavesOutThatRevisionAndReportsIt()
    {
        var result = FileReservationContract.ReplacePaths(WithRevisions,
            path => path == "/files/a.pdf" ? null : ToCopy(path));

        result.MissingFiles.Should().Equal("v1.pdf (older version)");
        var value = JsonNode.Parse(result.Value!)!.AsObject();
        value["path"]!.GetValue<string>().Should().Be("/files/copy-c.pdf");
        value["revisions"]!.AsArray().Select(r => r!["revisionId"]!.GetValue<string>()).Should().Equal("r2");
    }

    [Fact]
    public void ReplacePaths_EveryRevisionMissing_RemovesTheRevisionsKey()
    {
        var result = FileReservationContract.ReplacePaths(WithRevisions,
            path => path == "/files/c.pdf" ? ToCopy(path) : null);

        result.MissingFiles.Should().HaveCount(2);
        JsonNode.Parse(result.Value!)!.AsObject().ContainsKey("revisions").Should().BeFalse();
    }

    [Fact]
    public void ReplacePaths_CurrentFileMissing_LeavesOutTheWholeAttachment()
    {
        var result = FileReservationContract.ReplacePaths(WithRevisions,
            path => path == "/files/c.pdf" ? null : ToCopy(path));

        result.Value.Should().BeNull();
        result.MissingFiles.Should().Equal("v3.pdf");
    }

    [Fact]
    public void ReplacePaths_OneOfTwoAttachmentsMissing_KeepsTheOther()
    {
        const string value = """[{"name":"x.png","path":"/files/x.png"},{"name":"y.png","path":"/files/y.png"}]""";

        var result = FileReservationContract.ReplacePaths(value, path => path == "/files/x.png" ? null : ToCopy(path));

        result.MissingFiles.Should().Equal("x.png");
        var attachments = JsonNode.Parse(result.Value!)!.AsArray();
        attachments.Should().HaveCount(1);
        attachments[0]!["path"]!.GetValue<string>().Should().Be("/files/copy-y.png");
    }

    [Fact]
    public void ReplacePaths_MissingFileWithoutName_ReportsTheFileNameOfItsPath()
    {
        var result = FileReservationContract.ReplacePaths("""{"path":"/files/abc.txt"}""", _ => null);

        result.MissingFiles.Should().Equal("abc.txt");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    public void ReplacePaths_NotAFileValue_ReturnsItUnchanged(string? value)
    {
        var result = FileReservationContract.ReplacePaths(value, ToCopy);

        result.Value.Should().Be(value);
        result.MissingFiles.Should().BeEmpty();
    }
}
