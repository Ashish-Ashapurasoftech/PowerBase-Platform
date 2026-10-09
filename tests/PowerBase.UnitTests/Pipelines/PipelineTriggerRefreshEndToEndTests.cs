using System.Data;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// End to end regression for the reported case: an Order is saved together with its Order Detail rows, the
/// "record added" event carries a blank "Sum of Amount" (the details are written a moment later), and a
/// Condition "Sum of Amount &gt; 100" must still take the "met" branch when the PowerFlow runs. Drives the real
/// engine from <see cref="PipelineEngine.ExecuteAsync"/> through the On New Event trigger and the Condition.
/// </summary>
public class PipelineTriggerRefreshEndToEndTests
{
    private const long SummaryFid = 14;
    private static readonly Guid TableId = Guid.NewGuid();
    private static readonly Guid RecordId = Guid.NewGuid();

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class Run
    {
        public CapturingLogger<PipelineEngine> Log { get; } = new();
        public List<PipelineStepRun> StepRuns { get; } = new();
        public PipelineRun? StoredRun { get; set; }
        public Guid ConditionStepId { get; set; }
        public Dictionary<Guid, string> Outputs { get; } = new();
    }

    private static async Task<Run> ExecuteAsync(object? summaryInEvent, object? currentSummary, string eventType = "Added", bool refreshFails = false, bool summaryKeyInEvent = true)
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var idempotency = Substitute.For<IPipelineStepIdempotencyRepository>();
        var relational = Substitute.For<IRelationalProjector>();
        var result = new Run();
        var formula = Substitute.For<IFormulaProjector>();

        var table = new AppTable { Id = 5, AppId = 1, PublicId = TableId };
        var fields = new List<AppField>
        {
            new() { Id = 6, Fid = 6, Name = "Order no", TypeCode = "Text" },
            new() { Id = SummaryFid, Fid = (int)SummaryFid, Name = "Sum of Amount", TypeCode = "Summary",
                    Settings = "{\"function\":\"Sum\",\"targetTypeCode\":\"Formula_Number\"}" },
        };
        tableRepo.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(fields);
        recordRepo.GetRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long> { [RecordId] = 3 });
        if (refreshFails)
            recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                .Returns<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>>(_ => throw new InvalidOperationException("db down"));
        else
            recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [3] = new Dictionary<string, object?> { ["Id"] = 3L, ["f_6"] = "ORD0003" } });
        relational.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(new List<IReadOnlyDictionary<long, object?>>
                { new Dictionary<long, object?> { [SummaryFid] = currentSummary } }));
        formula.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(2)!);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IRelationalProjector)).Returns(relational);
        serviceProvider.GetService(typeof(IFormulaProjector)).Returns(formula);
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());

        var engine = new PipelineEngine(
            pipelineRepo, recordRepo, Substitute.For<IRecordWriteService>(), tableRepo, fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
            result.Log, Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), idempotency);

        var messageId = Guid.NewGuid();
        var trigger = new PipelineStep
        {
            Id = 1, PipelineId = 1, PublicId = Guid.NewGuid(), RefId = "ref_1", Type = "trigger", Subtype = "new-event", DisplayOrder = 1,
            ConfigJson = JsonSerializer.Serialize(new { TablePublicId = TableId.ToString() })
        };
        var condition = new PipelineStep
        {
            Id = 2, PipelineId = 1, PublicId = Guid.NewGuid(), RefId = "ref_2", Type = "condition", Subtype = "if-else", DisplayOrder = 2,
            ConfigJson = JsonSerializer.Serialize(new { leftOperand = "{{steps.ref_1.fid_14}}", @operator = "greater_than", rightOperand = "100" })
        };

        pipelineRepo.GetRunByMessageIdAsync(messageId, Arg.Any<CancellationToken>()).Returns(_ => result.StoredRun);
        pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns(call => { result.StoredRun = call.Arg<PipelineRun>(); return (Guid.NewGuid(), 1L); });
        pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });
        pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { trigger, condition });
        pipelineRepo.CreateRunAttemptAsync(Arg.Any<PipelineRunAttempt>(), Arg.Any<CancellationToken>()).Returns(1L);
        pipelineRepo.CreateStepRunAsync(Arg.Do<PipelineStepRun>(result.StepRuns.Add), Arg.Any<CancellationToken>()).Returns(1L);
        result.ConditionStepId = condition.PublicId;
        idempotency.GetByExecutionKeyAsync(messageId, Arg.Any<Guid>(), Arg.Any<byte[]>(), null, Arg.Any<CancellationToken>()).Returns((string?)null);
        idempotency.When(x => x.InsertAsync(Arg.Any<PipelineStepIdempotencyLog>(), null, Arg.Any<CancellationToken>()))
            .Do(call => { var entry = call.Arg<PipelineStepIdempotencyLog>(); result.Outputs[entry.StepPublicId] = entry.OutputJson; });

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["EventType"] = eventType,
            ["TriggerStepRefId"] = "ref_1",
            ["TablePublicId"] = TableId.ToString(),
            ["RecordPublicId"] = RecordId.ToString(),
            ["SelectedFieldValues"] = EventValues(summaryInEvent, summaryKeyInEvent),
            ["NewValues"] = EventValues(summaryInEvent, summaryKeyInEvent),
        });
        var task = new PipelineExecutionTask
        {
            PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = payload, MessageId = messageId.ToString(), WorkerId = "w"
        };

        await engine.ExecuteAsync(task, CancellationToken.None);
        return result;
    }

    /// <summary>What the Condition actually compared and decided, from the engine's own log line.</summary>
    private static Dictionary<string, object?> EventValues(object? summary, bool keyPresent)
    {
        var values = new Dictionary<string, object?> { ["fid_6"] = "ORD0003" };
        if (keyPresent) values["fid_14"] = summary;
        return values;
    }

    private static string ConditionDecision(Run run) =>
        run.Log.Messages.LastOrDefault(m => m.StartsWith("Condition step")) ?? throw new Xunit.Sdk.XunitException($"no condition decision logged; run={run.StoredRun?.Status}; log={string.Join(" | ", run.Log.Messages.TakeLast(5))}");

    [Fact]
    public async Task OrderSavedWithItsDetails_BlankSummaryInTheEvent_ConditionTakesTheMetBranch()
    {
        var run = await ExecuteAsync(summaryInEvent: null, currentSummary: 1000m);

        var decision = ConditionDecision(run);
        Assert.Contains("Left: '1000'", decision);
        Assert.Contains("Result: True", decision);
    }

    [Fact]
    public async Task EventWithoutTheSummaryAtAll_AsWrittenNow_ConditionStillTakesTheMetBranch()
    {
        // The write no longer computes Lookup / Summary values, so the event has no "fid_14" key whatsoever
        // (this is the payload that made every Order go to the "not met" branch, even with a Sum of 1800).
        var run = await ExecuteAsync(summaryInEvent: null, currentSummary: 1800m, summaryKeyInEvent: false);

        var decision = ConditionDecision(run);
        Assert.Contains("Left: '1800'", decision);
        Assert.Contains("Result: True", decision);
    }

    [Fact]
    public async Task EventWithoutTheSummaryAtAll_AndASmallSum_TakesTheNotMetBranch()
    {
        var run = await ExecuteAsync(summaryInEvent: null, currentSummary: 50m, summaryKeyInEvent: false);

        Assert.Contains("Result: False", ConditionDecision(run));
    }

    [Fact]
    public async Task SummaryStillBelowTheLimit_ConditionTakesTheNotMetBranch()
    {
        var run = await ExecuteAsync(summaryInEvent: null, currentSummary: 50m);

        Assert.Contains("Result: False", ConditionDecision(run));
    }

    [Fact]
    public async Task EventAlreadyCarriedAValue_TheCurrentValueWins()
    {
        // The event said 50 (before more details were saved); by run time the Summary is 1000.
        var run = await ExecuteAsync(summaryInEvent: 50m, currentSummary: 1000m);

        Assert.Contains("Result: True", ConditionDecision(run));
    }

    [Fact]
    public async Task WhenTheRereadFails_TheRunStillCompletesWithTheEventValue()
    {
        var run = await ExecuteAsync(summaryInEvent: 250m, currentSummary: 1000m, refreshFails: true);

        Assert.Equal("Success", run.StoredRun!.Status);
        Assert.Contains("Result: True", ConditionDecision(run));       // 250 > 100 from the event itself
    }

    [Fact]
    public async Task DeletedEvent_IsNotRefreshed_TheEventValueIsUsed()
    {
        var run = await ExecuteAsync(summaryInEvent: 50m, currentSummary: 1000m, eventType: "Deleted");

        Assert.Contains("Result: False", ConditionDecision(run));
    }
}
