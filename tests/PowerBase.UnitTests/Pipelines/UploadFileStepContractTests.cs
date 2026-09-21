using System.Text;
using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class UploadFileStepContractTests
{
    [Theory]
    [InlineData("https://files.example.com/report.pdf")]
    [InlineData("{\"file_transfer_handle\":\"https://files.example.com/report.pdf\"}")]
    [InlineData("{\"file\":{\"Path\":\"https://files.example.com/report.pdf\"}}")]
    public void ResolveDownloadUri_AcceptsPublicUrlsAndTransferHandles(string value)
    {
        UploadFileStepContract.ResolveDownloadSource(value)
            .Should().Be("https://files.example.com/report.pdf");
    }

    [Fact]
    public void ResolveDownloadUri_RejectsUnsupportedSources()
    {
        var action = () => UploadFileStepContract.ResolveDownloadSource("C:\\temp\\report.pdf");
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ResolveDownloadSource_ReadsRelativePathFromStoredAttachment()
    {
        const string handle = "{\"name\":\"report.pdf\",\"path\":\"/files/report.pdf\",\"size\":123}";

        UploadFileStepContract.ResolveDownloadSource(handle).Should().Be("/files/report.pdf");
    }

    [Fact]
    public void NormalizeFileSourceToken_UsesRawAttachmentForBrowserUrlReference()
    {
        UploadFileStepContract.NormalizeFileSourceToken(
                "{{(steps.ref_lookup.fid_7 | from_json).path}}")
            .Should().Be("{{steps.ref_lookup.fid_7}}");
    }

    [Theory]
    [InlineData("photo", "https://files.example.com/download", null, "image/jpeg", "photo.jpg")]
    [InlineData("photo", "https://files.example.com/source.png", null, "application/octet-stream", "photo.png")]
    [InlineData("photo", "https://files.example.com/download", "camera.webp", "application/octet-stream", "photo.webp")]
    [InlineData("photo.jpeg", "https://files.example.com/download", null, "image/png", "photo.jpeg")]
    public void EnsureFileExtension_KeepsFilesServeable(
        string fileName,
        string sourceUrl,
        string? responseFileName,
        string? contentType,
        string expected)
    {
        UploadFileStepContract.EnsureFileExtension(
                fileName, new Uri(sourceUrl), responseFileName, contentType)
            .Should().Be(expected);
    }

    [Fact]
    public void EnsureFileExtension_DetectsJpegWhenHeadersHaveNoTypeOrName()
    {
        using var content = new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        UploadFileStepContract.EnsureFileExtension(
                "photo", new Uri("https://files.example.com/download"), null,
                "application/octet-stream", content)
            .Should().Be("photo.jpg");
    }

    [Theory]
    [InlineData("surat", "https://raw.githubusercontent.com/cs109/2014_data/master/countries.csv", null, "countries.csv")]
    [InlineData("fallback", "https://files.example.com/download", "monthly-report.csv", "monthly-report.csv")]
    [InlineData("fallback", "https://files.example.com/download", null, "fallback")]
    public void PreferSourceFileName_UsesActualRemoteNameWhenAvailable(
        string configuredName,
        string sourceUrl,
        string? responseFileName,
        string expected)
    {
        UploadFileStepContract.PreferSourceFileName(
                configuredName, new Uri(sourceUrl), responseFileName)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("archive.7z")]
    [InlineData("installer.exe")]
    [InlineData("script.ps1")]
    [InlineData("document.customextension")]
    public void EnsureFileExtension_AllowsAnyFileType(string fileName)
    {
        UploadFileStepContract.EnsureFileExtension(
                fileName, new Uri("https://files.example.com/download"), null, null)
            .Should().Be(fileName);
    }

    [Fact]
    public void NormalizeFileName_EnforcesQuickbase225CharacterLimit()
    {
        UploadFileStepContract.NormalizeFileName(new string('a', 225)).Should().HaveLength(225);

        var action = () => UploadFileStepContract.NormalizeFileName(new string('a', 226));
        action.Should().Throw<InvalidOperationException>().WithMessage("*225 characters*");
    }

    [Fact]
    public async Task ReadWithLimitAsync_RejectsFilesOverQuickbaseLimit()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("file"));
        content.Headers.ContentLength = UploadFileStepContract.MaxFileSizeBytes + 1;
        var action = () => UploadFileStepContract.ReadWithLimitAsync(content, CancellationToken.None);
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*71 MB*");
    }

    [Fact]
    public void ResolveRecordPublicId_ReadsPipelineRecordOutputs()
    {
        var id = Guid.NewGuid();
        UploadFileStepContract.ResolveRecordPublicId(new { CreatedRecordPublicId = id })
            .Should().Be(id);
        UploadFileStepContract.ResolveRecordPublicId(new { item = new { RecordPublicId = id } })
            .Should().Be(id);
    }

    [Fact]
    public void SerializeAttachmentValue_MatchesAddRecordFileShape()
    {
        var value = UploadFileStepContract.SerializeAttachmentValue(new StoredFile
        {
            Name = "report.pdf",
            Path = "/files/report.pdf",
            Size = 123,
            ContentType = "application/pdf"
        });

        using var document = JsonDocument.Parse(value);
        var attachment = document.RootElement;
        attachment.GetProperty("name").GetString().Should().Be("report.pdf");
        attachment.GetProperty("path").GetString().Should().Be("/files/report.pdf");
        attachment.GetProperty("size").GetInt64().Should().Be(123);
        attachment.GetProperty("type").GetString().Should().Be("application/pdf");
        attachment.TryGetProperty("Path", out _).Should().BeFalse();
        attachment.TryGetProperty("ContentType", out _).Should().BeFalse();
    }
}
