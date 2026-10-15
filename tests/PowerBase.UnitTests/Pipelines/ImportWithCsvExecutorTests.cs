using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

public class ImportWithCsvExecutorTests
{
    private readonly ImportWithCsvFakes _import = new();
    private readonly FakeReadableStorage _storage = new();
    private StubHttpHandler _http = new(_ => StubHttpHandler.Csv("a,b\n1,2"));

    private ImportWithCsvExecutor Executor(FakeReadableStorage? primary = null, FakeReadableStorage? fallback = null) =>
        new(_import.Services(primary ?? _storage), fallback,
            (uri, ct) => new HttpClient(_http).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct));

    private static ImportWithCsvDefinition Config(string? csvFile = null, string? upload = null) =>
        new() { App = Guid.NewGuid().ToString(), ImportTemplateId = Guid.NewGuid().ToString(), CsvFile = csvFile, CsvUpload = upload };

    private static async Task<string> Text(ImportCsvFile file)
    {
        using var reader = new StreamReader(file.Content, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    // ---- the step's configuration ----

    [Fact]
    public void Definition_ReadsIdsWithContextPrefixAndFallsBackToTheOldUrlField()
    {
        var app = Guid.NewGuid(); var template = Guid.NewGuid();
        var config = ImportWithCsvDefinition.Read(JsonSerializer.Serialize(new { app = $"conn:{app}", importTemplateId = template, csvUrl = "https://x/a.csv" }));

        config.AppGuid.Should().Be(app);
        config.TemplateGuid.Should().Be(template);
        config.FileSource.Should().Be("https://x/a.csv");
        config.HasUpload.Should().BeFalse();
        config.Validate();
    }

    [Fact]
    public void Definition_PrefersTheNewFileFieldOverTheOldUrl()
    {
        ImportWithCsvDefinition.Read("{\"csvFile\":\"{{steps.ref_1.fid_6}}\",\"csvUrl\":\"https://old\"}").FileSource.Should().Be("{{steps.ref_1.fid_6}}");
    }

    [Theory]
    [InlineData("{\"importTemplateId\":\"" + "11111111-1111-1111-1111-111111111111" + "\",\"csvFile\":\"x\"}", "requires an app")]
    [InlineData("{\"app\":\"11111111-1111-1111-1111-111111111111\",\"csvFile\":\"x\"}", "requires an import template")]
    [InlineData("{\"app\":\"11111111-1111-1111-1111-111111111111\",\"importTemplateId\":\"11111111-1111-1111-1111-111111111111\"}", "requires a CSV or Excel file")]
    public void Definition_ValidateNamesWhatIsMissing(string json, string message)
    {
        var act = () => ImportWithCsvDefinition.Read(json).Validate();
        act.Should().Throw<PipelineNonRetryableException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Definition_InvalidJsonFailsTheStep()
    {
        var act = () => ImportWithCsvDefinition.Read("{bad");
        act.Should().Throw<PipelineNonRetryableException>();
    }

    [Fact]
    public void Definition_AnUploadIsEnoughWithoutAUrl()
    {
        Config(upload: "[{\"name\":\"a.csv\",\"path\":\"/files/a.csv\"}]").Validate();
    }

    // ---- getting the file ----

    [Fact]
    public async Task UploadedFile_IsReadFromStorageWithItsOriginalName()
    {
        _storage.Files["/files/9f2-stored.csv"] = Encoding.UTF8.GetBytes("Name\nAnn");
        await using var file = await Executor().OpenFileAsync(Config(upload: "[{\"name\":\"people.csv\",\"path\":\"/files/9f2-stored.csv\",\"size\":8}]"), null, default);

        file.FileName.Should().Be("people.csv");
        file.Length.Should().Be(8);
        (await Text(file)).Should().Be("Name\nAnn");
    }

    [Fact]
    public async Task UploadedFile_FallsBackToTheOwnersStorageWhenTheStepRunsInAnotherAccount()
    {
        var owner = new FakeReadableStorage();
        owner.Files["/files/a.csv"] = Encoding.UTF8.GetBytes("x");
        await using var file = await Executor(new FakeReadableStorage(), owner).OpenFileAsync(Config(upload: "{\"name\":\"a.csv\",\"path\":\"/files/a.csv\"}"), null, default);
        (await Text(file)).Should().Be("x");
    }

    [Fact]
    public async Task MissingStoredFile_FailsWithoutRetry()
    {
        var act = () => Executor().OpenFileAsync(Config(upload: "{\"name\":\"a.csv\",\"path\":\"/files/gone.csv\"}"), null, default);
        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*could not be found*");
    }

    [Fact]
    public async Task FileFromAnEarlierStep_UsesThePathAndNameOfTheFileValue()
    {
        _storage.Files["/files/x1"] = Encoding.UTF8.GetBytes("a,b");
        await using var file = await Executor().OpenFileAsync(Config("{{steps.ref_1.fid_6}}"), "{\"name\":\"Orders.csv\",\"path\":\"/files/x1\",\"size\":3}", default);
        file.FileName.Should().Be("Orders.csv");
    }

    [Fact]
    public async Task StoredPath_KeepsItsFileName()
    {
        _storage.Files["/files/data.xlsx"] = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1 };
        await using var file = await Executor().OpenFileAsync(Config("x"), "/files/data.xlsx", default);
        file.FileName.Should().Be("data.xlsx");
    }

    [Fact]
    public async Task Url_IsDownloadedUsingTheServersFileName()
    {
        _http = new StubHttpHandler(_ => StubHttpHandler.Csv("a,b\n1,2", "export.csv"));
        await using var file = await Executor().OpenFileAsync(Config("x"), "https://files.example.com/download?id=5", default);

        file.FileName.Should().Be("export.csv");
        (await Text(file)).Should().Be("a,b\n1,2");
        _http.Requested.Should().ContainSingle().Which.AbsoluteUri.Should().Be("https://files.example.com/download?id=5");
    }

    [Fact]
    public async Task Url_WithoutAServerFileNameUsesTheUrlName()
    {
        await using var file = await Executor().OpenFileAsync(Config("x"), "https://files.example.com/in/people.csv", default);
        file.FileName.Should().Be("people.csv");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Url_ThatIsMissingOrForbidden_FailsWithoutRetry(HttpStatusCode status)
    {
        _http = new StubHttpHandler(_ => StubHttpHandler.Csv("", status: status));
        var act = () => Executor().OpenFileAsync(Config("x"), "https://files.example.com/a.csv", default);
        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage($"*{(int)status}*");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Url_WithATemporaryServerProblem_IsRetried(HttpStatusCode status)
    {
        _http = new StubHttpHandler(_ => StubHttpHandler.Csv("", status: status));
        var act = () => Executor().OpenFileAsync(Config("x"), "https://files.example.com/a.csv", default);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Url_LargerThanTheImportLimit_FailsBeforeDownloading()
    {
        _http = new StubHttpHandler(_ =>
        {
            var response = StubHttpHandler.Csv("a");
            response.Content.Headers.ContentLength = ImportFileFormats.MaxFileBytes + 1;
            return response;
        });
        var act = () => Executor().OpenFileAsync(Config("x"), "https://files.example.com/a.csv", default);
        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*larger than*");
    }

    [Fact]
    public async Task EmptyFile_FailsWithoutRetry()
    {
        _http = new StubHttpHandler(_ => StubHttpHandler.Csv(""));
        var act = () => Executor().OpenFileAsync(Config("x"), "https://files.example.com/a.csv", default);
        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*empty*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just text")]
    [InlineData("ftp://files.example.com/a.csv")]
    [InlineData("https://user:secret@files.example.com/a.csv")]
    [InlineData("{\"name\":\"no path\"}")]
    public async Task UnusableSource_FailsWithoutRetry(string resolved)
    {
        var act = () => Executor().OpenFileAsync(Config("x"), resolved, default);
        await act.Should().ThrowAsync<PipelineNonRetryableException>();
        _http.Requested.Should().BeEmpty();
    }

    [Theory]
    [InlineData("people.csv", null, "a,b", "people.csv")]
    [InlineData("Book.XLSX", null, "x", "Book.XLSX")]
    [InlineData("old.xls", null, "x", "old.xls")]
    [InlineData("export", null, "a,b", "export.csv")]
    [InlineData("report.txt", null, "a,b", "report.txt.csv")]
    [InlineData("", null, "a,b", "import.csv")]
    [InlineData("download", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "x", "download.xlsx")]
    public void ImportFileName_GivesTheImportANameItRecognises(string name, string? mediaType, string body, string expected)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ImportWithCsvExecutor.ImportFileName(name, mediaType, content).Should().Be(expected);
    }

    [Fact]
    public void ImportFileName_RecognisesAWorkbookByItsContent()
    {
        using var content = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 9, 9 });
        ImportWithCsvExecutor.ImportFileName("sheet", null, content).Should().Be("sheet.xlsx");
        content.Position.Should().Be(0);
    }

    // ---- starting and following the import ----

    [Fact]
    public async Task Start_HandsTheFileToTheImportRunCode()
    {
        _storage.Files["/files/a.csv"] = Encoding.UTF8.GetBytes("Name\nAnn");
        var executor = Executor();
        await using var file = await executor.OpenFileAsync(Config(upload: "{\"name\":\"people.csv\",\"path\":\"/files/a.csv\"}"), null, default);

        var started = await executor.StartAsync(_import.AppId, _import.TemplateId, file, "a@x.com; b@x.com", "pf-token", null, default);

        started.RunId.Should().Be(_import.RunId);
        _import.UploadedName.Should().Be("people.csv");
        Encoding.UTF8.GetString(_import.UploadedBytes!).Should().Be("Name\nAnn");
        _import.StartedEmails.Should().BeEquivalentTo("a@x.com", "b@x.com");
        _import.StartedToken.Should().Be("pf-token");
    }

    [Fact]
    public async Task Start_WithoutEmailsKeepsThePeopleSavedOnTheImport()
    {
        _storage.Files["/files/a.csv"] = Encoding.UTF8.GetBytes("x");
        var executor = Executor();
        await using var file = await executor.OpenFileAsync(Config(upload: "{\"name\":\"a.csv\",\"path\":\"/files/a.csv\"}"), null, default);
        await executor.StartAsync(_import.AppId, _import.TemplateId, file, "  ", null, null, default);
        _import.StartedEmails.Should().BeNull();
    }

    [Fact]
    public async Task Start_OfAnImportThatNoLongerExists_FailsWithAClearMessage()
    {
        _storage.Files["/files/a.csv"] = Encoding.UTF8.GetBytes("x");
        var executor = Executor();
        await using var file = await executor.OpenFileAsync(Config(upload: "{\"name\":\"a.csv\",\"path\":\"/files/a.csv\"}"), null, default);
        var act = () => executor.StartAsync(_import.AppId, Guid.NewGuid(), file, null, null, null, default);
        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*import template was not found*");
    }

    [Fact]
    public async Task Start_OfATableImport_IsRefusedByTheImportRules()
    {
        var tableImport = new ImportWithCsvFakes("table");
        _storage.Files["/files/a.csv"] = Encoding.UTF8.GetBytes("x");
        var executor = new ImportWithCsvExecutor(tableImport.Services(_storage), null, (_, _) => throw new InvalidOperationException());
        await using var file = await executor.OpenFileAsync(Config(upload: "{\"name\":\"a.csv\",\"path\":\"/files/a.csv\"}"), null, default);
        var act = () => executor.StartAsync(tableImport.AppId, tableImport.TemplateId, file, null, null, null, default);
        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*could not start*reads a table*");
    }

    [Theory]
    [InlineData("This import is already running.")]
    [InlineData("This import needs attention before it can run: a mapped field changed.")]
    public async Task Start_ThatTheImportRefuses_FailsWithItsMessage(string reason)
    {
        _import.RejectStart(new ConflictException(reason));
        _storage.Files["/files/a.csv"] = Encoding.UTF8.GetBytes("x");
        var executor = Executor();
        await using var file = await executor.OpenFileAsync(Config(upload: "{\"name\":\"a.csv\",\"path\":\"/files/a.csv\"}"), null, default);
        var act = () => executor.StartAsync(_import.AppId, _import.TemplateId, file, null, null, null, default);
        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage($"*{reason}*");
    }

    [Fact]
    public async Task Status_ReadsTheRunCounts()
    {
        _import.Finish(ImportRunStatus.Partial, read: 10, inserted: 6, updated: 2, unchanged: 1, errored: 1, skipped: 0);
        var status = await Executor().GetStatusAsync(_import.AppId, _import.RunId, default);
        status.Status.Should().Be("partial");
        status.RowsRead.Should().Be(10);
        status.Inserted.Should().Be(6);
    }

    [Fact]
    public async Task Status_OfARunThatIsGone_FailsWithoutRetry()
    {
        var act = () => Executor().GetStatusAsync(_import.AppId, Guid.NewGuid(), default);
        await act.Should().ThrowAsync<PipelineNonRetryableException>().WithMessage("*no longer available*");
    }

    [Theory]
    [InlineData("queued", false)]
    [InlineData("running", false)]
    [InlineData("success", true)]
    [InlineData("partial", true)]
    [InlineData("failed", true)]
    [InlineData("Cancelled", true)]
    [InlineData(null, false)]
    public void IsFinished_KnowsTheEndStates(string? status, bool finished) =>
        ImportWithCsvExecutor.IsFinished(status).Should().Be(finished);

    [Theory]
    [InlineData("failed", "Bad header row")]
    [InlineData("cancelled", null)]
    public void EnsureSucceeded_FailsTheStepForAFailedOrCancelledImport(string status, string? detail)
    {
        var response = new ImportRunStatusResponse(Guid.NewGuid(), status, 100, 0, 0, 0, 0, 0, null, null, detail, false);
        var act = () => ImportWithCsvExecutor.EnsureSucceeded(response);
        act.Should().Throw<PipelineNonRetryableException>().WithMessage($"*{status}*{detail}*");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("partial")]
    public void EnsureSucceeded_CompletesTheStepForSuccessAndPartial(string status) =>
        ImportWithCsvExecutor.EnsureSucceeded(new ImportRunStatusResponse(Guid.NewGuid(), status, 100, 0, 0, 0, 0, 0, null, null, null, false));

    [Fact]
    public void Output_HasEveryFieldLaterStepsRead()
    {
        var runId = Guid.NewGuid();
        var json = ImportWithCsvExecutor.Output(new ImportRunStatusResponse(runId, "partial", 100, 10, 6, 2, 1, 1, null, null, null, true, Unchanged: 1));
        using var output = JsonDocument.Parse(json);
        var root = output.RootElement;
        root.GetProperty("RecordsProcessed").GetInt64().Should().Be(10);
        root.GetProperty("RecordsAdded").GetInt64().Should().Be(6);
        root.GetProperty("RecordsUpdated").GetInt64().Should().Be(2);
        root.GetProperty("RecordsUnchanged").GetInt64().Should().Be(1);
        root.GetProperty("RecordsSkipped").GetInt64().Should().Be(1);
        root.GetProperty("RecordsErrored").GetInt64().Should().Be(1);
        root.GetProperty("ImportStatus").GetString().Should().Be("partial");
        root.GetProperty("ImportRunId").GetGuid().Should().Be(runId);
        json.Should().NotContain("\"Status\":\"Skipped\"", "the engine reads that as a skipped step");
    }

    [Fact]
    public void ClientToken_IsTheSameForARetryAndDifferentForAnotherIteration()
    {
        var message = Guid.NewGuid(); var step = Guid.NewGuid();
        var token = ImportWithCsvExecutor.ClientToken(message, step, "main/ref_1");
        ImportWithCsvExecutor.ClientToken(message, step, "main/ref_1").Should().Be(token);
        ImportWithCsvExecutor.ClientToken(message, step, "main/ref_loop/loop_index_1/ref_1").Should().NotBe(token);
        ImportWithCsvExecutor.ClientToken(Guid.NewGuid(), step, "main/ref_1").Should().NotBe(token);
        token.Length.Should().BeLessThanOrEqualTo(StartPowerFlowImportHandler.MaxClientTokenLength);
    }

    [Fact]
    public void RunReceipt_RoundTrips()
    {
        var receipt = new ImportWithCsvRunReceipt(Guid.NewGuid(), "a.csv", 12);
        ImportWithCsvExecutor.TryReadRunReceipt(ImportWithCsvExecutor.RunReceipt(receipt), out var read).Should().BeTrue();
        read.Should().Be(receipt);
        ImportWithCsvExecutor.TryReadRunReceipt(null, out _).Should().BeFalse();
        ImportWithCsvExecutor.TryReadRunReceipt("{\"Status\":\"Success\"}", out _).Should().BeFalse();
        ImportWithCsvExecutor.TryReadRunReceipt("not json", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("[{\"name\":\"a.csv\",\"path\":\"/files/a\"},{\"name\":\"b.csv\",\"path\":\"/files/b\"}]", "/files/a", "a.csv")]
    [InlineData("{\"Name\":\"a.csv\",\"Path\":\"/files/a\"}", "/files/a", "a.csv")]
    [InlineData("{\"url\":\"https://x/a.csv\"}", "https://x/a.csv", null)]
    public void ReadFileValue_ReadsFileFieldJson(string json, string path, string? name)
    {
        ImportWithCsvExecutor.ReadFileValue(json).Should().Be((path, name));
    }

    [Theory]
    [InlineData("https://x/a.csv")]
    [InlineData("[]")]
    [InlineData("{\"name\":\"a\"}")]
    [InlineData("{bad")]
    public void ReadFileValue_IsNullForAnythingElse(string value) => ImportWithCsvExecutor.ReadFileValue(value).Should().BeNull();
}
