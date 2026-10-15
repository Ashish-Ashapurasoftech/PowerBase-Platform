using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>The Import with CSV step inside real PowerFlow runs: it starts the import once, waits until it has finished (in the job,
/// or released as Waiting and resumed), hands its counts to later steps, fails with the import, and may be the first step.</summary>
public class PipelineEngineImportWithCsvTests
{
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineStepIdempotencyRepository _idempotencyRepo = Substitute.For<IPipelineStepIdempotencyRepository>();
    private readonly PipelineExecutionOptions _options = new();
    private readonly ImportWithCsvFakes _import = new();
    private readonly StubHttpHandler _http = new(_ => StubHttpHandler.Csv("Name,Email\nAnn,ann@x.com", "people.csv"));
    private readonly Dictionary<string, string> _receipts = new();
    private readonly PipelineEngine _engine;
    private PipelineRun? _run;

    private readonly PipelineStep _importStep;
    private readonly PipelineStep _following;

    public PipelineEngineImportWithCsvTests()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_http));
        var services = _import.Services(new FakeReadableStorage());
        services.GetService(typeof(IPipelineRecordSearchService)).Returns(Substitute.For<IPipelineRecordSearchService>());

        _engine = new PipelineEngine(
            _pipelineRepo, Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(), http, Substitute.For<IFileStorageService>(), Options.Create(_options),
            Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), services,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), _idempotencyRepo);

        _importStep = new PipelineStep
        {
            Id = 1, PipelineId = 1, PublicId = Guid.NewGuid(), RefId = "ref_import", Type = "action", Subtype = "import-with-csv", DisplayOrder = 0,
            ConfigJson = JsonSerializer.Serialize(new
            {
                connection = "", app = _import.AppId.ToString(), tableLabel = _import.TableId.ToString(),
                importTemplateId = _import.TemplateId.ToString(), csvFile = "https://files.example.com/people.csv", notifyEmails = "ops@x.com"
            })
        };
        _following = new PipelineStep { Id = 2, PipelineId = 1, PublicId = Guid.NewGuid(), RefId = "ref_next", Type = "trigger", Subtype = "schedule", DisplayOrder = 1 };

        _pipelineRepo.GetRunByMessageIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => _run);
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns(call => { _run = call.Arg<PipelineRun>(); return (Guid.NewGuid(), 1L); });
        _pipelineRepo.ClaimWaitingRunAsync(Arg.Any<Guid>(), "import-worker", Arg.Any<CancellationToken>())
            .Returns(_ => { _run!.LockedBy = "import-worker"; _run.Status = "Running"; return true; });
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false, CreatedBy = 42 });
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(_ => new List<PipelineStep> { _importStep, _following });
        _pipelineRepo.CreateRunAttemptAsync(Arg.Any<PipelineRunAttempt>(), Arg.Any<CancellationToken>()).Returns(1L);
        _pipelineRepo.CreateStepRunAsync(Arg.Any<PipelineStepRun>(), Arg.Any<CancellationToken>()).Returns(1L);
        _idempotencyRepo.GetByExecutionKeyAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<byte[]>(), null, Arg.Any<CancellationToken>())
            .Returns(call => _receipts.GetValueOrDefault(Key(call.ArgAt<Guid>(1), call.ArgAt<byte[]>(2))));
        _idempotencyRepo.When(x => x.InsertAsync(Arg.Any<PipelineStepIdempotencyLog>(), null, Arg.Any<CancellationToken>()))
            .Do(call => { var log = call.Arg<PipelineStepIdempotencyLog>(); _receipts[Key(log.StepPublicId, log.ExecutionPathHash)] = log.OutputJson; });
    }

    private static string Key(Guid step, byte[] hash) => step + ":" + Convert.ToHexString(hash);

    private static PipelineExecutionTask Task(string triggerEvent = "manual", Guid? messageId = null) => new()
    {
        PipelineId = 1, TenantId = 1, TriggerEvent = triggerEvent, TriggerPayloadJson = "{}",
        MessageId = (messageId ?? Guid.NewGuid()).ToString(), WorkerId = "import-worker"
    };

    /// <summary>What the step handed to later steps (its completion receipt).</summary>
    private JsonElement StepOutput()
    {
        var output = _receipts.Where(r => r.Key.StartsWith(_importStep.PublicId.ToString()))
            .Select(r => r.Value).Single(v => v.Contains("RecordsProcessed"));
        return JsonDocument.Parse(output).RootElement;
    }

    [Fact]
    public async Task FinishedImport_CompletesTheStepAndGivesItsCountsToLaterSteps()
    {
        _import.Finish(ImportRunStatus.Success, read: 4, inserted: 2, updated: 1, unchanged: 1);

        await _engine.ExecuteAsync(Task(), CancellationToken.None);

        _run!.Status.Should().Be("Success");
        _import.Starts.Should().Be(1);
        _import.UploadedName.Should().Be("people.csv");
        Encoding.UTF8.GetString(_import.UploadedBytes!).Should().Be("Name,Email\nAnn,ann@x.com");
        _import.StartedEmails.Should().BeEquivalentTo("ops@x.com");
        _import.StartedToken.Should().StartWith("pf-");
        var output = StepOutput();
        output.GetProperty("RecordsProcessed").GetInt64().Should().Be(4);
        output.GetProperty("RecordsAdded").GetInt64().Should().Be(2);
        output.GetProperty("RecordsUpdated").GetInt64().Should().Be(1);
        output.GetProperty("RecordsUnchanged").GetInt64().Should().Be(1);
        output.GetProperty("ImportStatus").GetString().Should().Be("success");
        output.GetProperty("ImportRunId").GetGuid().Should().Be(_import.RunId);
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(x => x.StepId == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunningImport_ReleasesTheJobAsWaitingAndTheResumeFollowsTheSameRun()
    {
        _options.PauseInlineMaxSeconds = 0; // release to the queue at once instead of waiting inside the job
        var task = Task();

        var wait = await Assert.ThrowsAsync<PipelineWaitException>(() => _engine.ExecuteAsync(task, CancellationToken.None));
        wait.ResumeDate.Should().BeAfter(DateTime.UtcNow);
        _run!.Status.Should().Be("Waiting");
        await _pipelineRepo.DidNotReceive().CreateStepRunAsync(Arg.Is<PipelineStepRun>(x => x.StepId == 2), Arg.Any<CancellationToken>());

        // Still running at the first resume: wait again, still the one import.
        await Assert.ThrowsAsync<PipelineWaitException>(() => _engine.ExecuteAsync(task, CancellationToken.None));

        _import.Finish(ImportRunStatus.Partial, read: 3, inserted: 1, updated: 0, unchanged: 0, errored: 2);
        await _engine.ExecuteAsync(task, CancellationToken.None);

        _run.Status.Should().Be("Success");
        _import.Starts.Should().Be(1, "a resume follows the run it started, never a second import");
        _http.Requested.Should().ContainSingle("the file is fetched once");
        StepOutput().GetProperty("RecordsErrored").GetInt64().Should().Be(2);
        StepOutput().GetProperty("ImportStatus").GetString().Should().Be("partial");
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(x => x.StepId == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShortImport_IsWaitedOutInsideTheJob()
    {
        var checks = 0;
        _import.Runs.GetByPublicIdAsync(_import.RunId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++checks == 2) _import.Finish(ImportRunStatus.Success);
            return _import.Run;
        });

        await _engine.ExecuteAsync(Task(), CancellationToken.None);

        checks.Should().Be(2);
        _run!.Status.Should().Be("Success");
    }

    [Theory]
    [InlineData("failed", "The header row does not match")]
    [InlineData("cancelled", null)]
    public async Task FailedOrCancelledImport_FailsTheRunWithTheImportsReason(string status, string? reason)
    {
        _import.Finish(status, error: reason);

        // The run is marked Failed and the error goes on to the queue, which does not retry a non-retryable step.
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.ExecuteAsync(Task(), CancellationToken.None));

        _run!.Status.Should().Be("Failed");
        _run.ErrorMessage.Should().Contain(status);
        if (reason != null) _run.ErrorMessage.Should().Contain(reason);
        await _pipelineRepo.DidNotReceive().CreateStepRunAsync(Arg.Is<PipelineStepRun>(x => x.StepId == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportThatCannotStart_FailsWithoutRetrying()
    {
        _import.RejectStart(new ConflictException("This import is already running."));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => _engine.ExecuteAsync(Task(), CancellationToken.None));
        error.Should().BeOfType<PipelineNonRetryableException>();

        _run!.Status.Should().Be("Failed");
        _run.ErrorMessage.Should().Contain("already running");
    }

    [Theory]
    [InlineData("activation")]
    [InlineData("pipeline_schedule")]
    public async Task ImportWithCsv_CanBeTheFirstStepOfAnOnDemandRun(string triggerEvent)
    {
        _import.Finish(ImportRunStatus.Success);
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { _importStep });

        await _engine.ExecuteAsync(Task(triggerEvent), CancellationToken.None);

        _run!.Status.Should().Be("Success");
        _import.Starts.Should().Be(1);
    }

    [Fact]
    public async Task UploadedFile_IsImportedWithItsName()
    {
        var storage = new FakeReadableStorage();
        storage.Files["/files/abc.csv"] = Encoding.UTF8.GetBytes("Name\nBob");
        _import.Services(storage, into: GetEngineServices());
        _importStep.ConfigJson = JsonSerializer.Serialize(new
        {
            app = _import.AppId.ToString(), tableLabel = _import.TableId.ToString(), importTemplateId = _import.TemplateId.ToString(),
            csvUpload = "[{\"name\":\"contacts.csv\",\"path\":\"/files/abc.csv\"}]"
        });
        _import.Finish(ImportRunStatus.Success);

        await _engine.ExecuteAsync(Task(), CancellationToken.None);

        _run!.Status.Should().Be("Success");
        _import.UploadedName.Should().Be("contacts.csv");
        Encoding.UTF8.GetString(_import.UploadedBytes!).Should().Be("Name\nBob");
        _http.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingImportTemplate_FailsTheRunBeforeAnyDownload()
    {
        _importStep.ConfigJson = JsonSerializer.Serialize(new { app = _import.AppId.ToString(), csvFile = "https://files.example.com/a.csv" });

        await Assert.ThrowsAnyAsync<Exception>(() => _engine.ExecuteAsync(Task(), CancellationToken.None));

        _run!.Status.Should().Be("Failed");
        _run.ErrorMessage.Should().Contain("import template");
        _http.Requested.Should().BeEmpty();
        _import.Starts.Should().Be(0);
    }

    private IServiceProvider GetEngineServices() =>
        (IServiceProvider)typeof(PipelineEngine).GetField("_serviceProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_engine)!;
}
