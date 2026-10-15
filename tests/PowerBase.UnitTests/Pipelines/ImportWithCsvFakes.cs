using System.Net;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>The import feature as the Import with CSV step meets it: its real start and status handlers (the same code as the
/// import run API) over fake repositories, recording what the step sent.</summary>
internal sealed class ImportWithCsvFakes
{
    public readonly Guid AppId = Guid.NewGuid(), TableId = Guid.NewGuid(), TemplateId = Guid.NewGuid(), RunId = Guid.NewGuid();
    public readonly IAppRepository Apps = Substitute.For<IAppRepository>();
    public readonly IAppTableRepository Tables = Substitute.For<IAppTableRepository>();
    public readonly IAppAccessService Access = Substitute.For<IAppAccessService>();
    public readonly IImportDefinitionRepository Definitions = Substitute.For<IImportDefinitionRepository>();
    public readonly IPowerFlowImportActions Actions = Substitute.For<IPowerFlowImportActions>();
    public readonly IImportRunRepository Runs = Substitute.For<IImportRunRepository>();
    public readonly IQueryContext User = Substitute.For<IQueryContext>();
    public readonly ImportRun Run;
    public string? UploadedName;
    public byte[]? UploadedBytes;
    public string? StartedToken;
    public IReadOnlyList<string>? StartedEmails;
    public int Starts;

    public ImportWithCsvFakes(string sourceKind = "file")
    {
        Apps.GetIdByPublicIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(5L);
        Definitions.GetByPublicIdAsync(TemplateId, Arg.Any<CancellationToken>())
            .Returns(new ImportDefinition { Id = 9, PublicId = TemplateId, DestinationTableId = 7, SourceKind = sourceKind });
        var table = new AppTable { Id = 7, AppId = 5, PublicId = TableId, Name = "People" };
        Tables.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(table);
        Tables.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(table);
        Actions.UploadAsync(TableId, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                using var copy = new MemoryStream();
                call.Arg<Stream>().CopyTo(copy);
                UploadedBytes = copy.ToArray();
                UploadedName = call.ArgAt<string>(2);
                return Guid.NewGuid();
            });
        Actions.StartAsync(TemplateId, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), AppId, Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Starts++;
                StartedEmails = call.ArgAt<IReadOnlyList<string>?>(1);
                StartedToken = call.ArgAt<string?>(4);
                return new ImportRunStart(RunId, false);
            });
        Run = new ImportRun { PublicId = RunId, Status = ImportRunStatus.Running, DefinitionSnapshotJson = ImportJson.Serialize(new { destinationTableId = TableId }) };
        Runs.GetByPublicIdAsync(RunId, Arg.Any<CancellationToken>()).Returns(_ => Run);
    }

    public void Finish(string status, long read = 4, long inserted = 2, long updated = 1, long unchanged = 1, long errored = 0, long skipped = 0, string? error = null)
    {
        Run.Status = status;
        Run.RowsRead = read; Run.Inserted = inserted; Run.Updated = updated; Run.Unchanged = unchanged;
        Run.Errored = errored; Run.Skipped = skipped; Run.ErrorDetail = error;
    }

    public void RejectStart(Exception ex) =>
        Actions.StartAsync(TemplateId, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), AppId, Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Throws(ex);

    public StartPowerFlowImportHandler StartHandler => new(Apps, Tables, Access, Definitions, Actions);
    public GetImportRunStatusHandler StatusHandler => new(Access, Runs, Tables, Apps, User);

    /// <summary>The step's services: what the engine hands the executor (the owner's or a connected account's scope).</summary>
    public IServiceProvider Services(IFileStorageService? storage = null, IServiceProvider? into = null)
    {
        var services = into ?? Substitute.For<IServiceProvider>();
        services.GetService(typeof(StartPowerFlowImportHandler)).Returns(_ => StartHandler);
        services.GetService(typeof(GetImportRunStatusHandler)).Returns(_ => StatusHandler);
        services.GetService(typeof(IFileStorageService)).Returns(storage);
        return services;
    }
}

/// <summary>File storage with readable files, keyed by stored path.</summary>
internal sealed class FakeReadableStorage : IFileStorageService, IFileStorageReadService
{
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
    public Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default, string? uniqueKey = null) =>
        throw new NotSupportedException();
    public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
        Files.TryGetValue(path, out var bytes) ? Task.FromResult<Stream>(new MemoryStream(bytes)) : throw new FileNotFoundException(path);
}

/// <summary>Answers every request with the given response.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public readonly List<Uri> Requested = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requested.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Csv(string body, string? fileName = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/csv") };
        if (fileName != null) response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = fileName };
        return response;
    }
}
