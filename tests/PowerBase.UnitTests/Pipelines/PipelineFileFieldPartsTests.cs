using FluentAssertions;
using PowerBase.Application.Pipelines;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineFileFieldPartsTests
{
    private const string Stored = "[{\"name\": \"invoice-2026.pdf\", \"path\": \"/files/test/invoice-2026.pdf\", \"size\": 10, \"contentType\": \"application/pdf\"}]";
    private const string App = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("file_transfer_handle", "/files/test/invoice-2026.pdf")]
    [InlineData("file_name", "invoice-2026.pdf")]
    [InlineData("file_size", "10")]
    public void Part_StoredArray_ReadsCurrentFile(string part, string expected)
        => PipelineFileFieldParts.Part(PipelineFileFieldParts.Read(Stored)!, part, null, App).Should().Be(expected);

    [Fact]
    public void Part_UploadStepObject_IsReadToo()
    {
        var file = PipelineFileFieldParts.Read("{\"name\":\"a.png\",\"path\":\"/files/x/a.png\",\"size\":5,\"type\":\"image/png\"}");
        PipelineFileFieldParts.Part(file!, "file_name", null, null).Should().Be("a.png");
    }

    [Fact]
    public void BrowserUrl_IsDownloadAddress_AndUploadReadsItBackFromStorage()
    {
        var url = PipelineFileFieldParts.Part(PipelineFileFieldParts.Read(Stored)!, "browser_url", "http://localhost:5159/", App);
        url.Should().Be($"http://localhost:5159/apps/{App}/files/download?path=%2Ffiles%2Ftest%2Finvoice-2026.pdf&fileName=invoice-2026.pdf");
        UploadFileStepContract.ResolveDownloadSource(url).Should().Be("/files/test/invoice-2026.pdf");
        var relative = PipelineFileFieldParts.Part(PipelineFileFieldParts.Read(Stored)!, "browser_url", null, App);
        UploadFileStepContract.ResolveDownloadSource(relative).Should().Be("/files/test/invoice-2026.pdf");
    }

    [Theory]
    [InlineData("[]")]
    public void EmptyField_GivesEmptyParts(string raw)
        => PipelineFileFieldParts.Part(PipelineFileFieldParts.Read(raw)!, "file_name", null, App).Should().BeEmpty();

    [Theory]
    [InlineData("hello")]
    [InlineData("42")]
    [InlineData("{\"street1\":\"x\"}")]
    public void NotAFile_IsNull(string raw) => PipelineFileFieldParts.Read(raw).Should().BeNull();

    [Theory]
    [InlineData("https://example.com/apps/11111111-2222-3333-4444-555555555555/files/download?path=..%2Fsecret")]
    [InlineData("https://example.com/a.pdf")]
    public void OtherUrls_AreNotStoredPaths(string url) => PipelineFileFieldParts.StoredPathOfDownloadUrl(url).Should().BeNull();
}
