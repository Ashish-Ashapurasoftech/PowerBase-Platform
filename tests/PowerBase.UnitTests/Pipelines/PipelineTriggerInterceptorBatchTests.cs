using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Infrastructure.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>Copy Records hands the events of a page of created records over together. That must produce exactly the events one-by-one interception
/// does, and fall back to one-by-one whenever a listener sees a batch differently from a single record.</summary>
public class PipelineTriggerInterceptorBatchTests
{
    private sealed class Interceptor : PipelineTriggerInterceptor
    {
        private readonly IReadOnlyList<(string? Subtype, bool LimitRecords)>? _listeners;

#pragma warning disable CS0618
        public Interceptor(IPipelineRepository repo, ITenantUnitOfWork uow, IReadOnlyList<(string? Subtype, bool LimitRecords)>? listeners)
            : base(repo, Substitute.For<IRecordRepository>(), Substitute.For<IQueryContext>(), uow, Substitute.For<ILogger<PipelineTriggerInterceptor>>())
            => _listeners = listeners;
#pragma warning restore CS0618

        protected override Task<IReadOnlyList<(string? Subtype, bool LimitRecords)>?> ListSubscriptionsAsync(AppTable table, CancellationToken ct)
            => Task.FromResult(_listeners);
    }

    private static (Interceptor Interceptor, IPipelineRepository Repo, AppTable Table, List<AppField> Fields) Create(
        IReadOnlyList<(string? Subtype, bool LimitRecords)>? listeners, string stepSubtype = "new-event", bool limited = false, int max = 1)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new() { Id = 10, Fid = 10, Name = "Selected", TypeCode = "Text" } };
        repo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { new() { Id = 101, IsActive = true } });
        repo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
        {
            new() { Type = "trigger", Subtype = stepSubtype, ConfigJson = JsonSerializer.Serialize(new
            {
                TablePublicId = table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnAnyField = true, LimitRecords = limited, MaxRecords = max
            }) }
        });
        return (new Interceptor(repo, uow, listeners), repo, table, fields);
    }

    private static List<(Guid RecordPublicId, IReadOnlyDictionary<long, object?> Values)> Records(int count) => Enumerable.Range(0, count)
        .Select(i => (Guid.NewGuid(), (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [10] = "v" + i })).ToList();

    private static readonly IReadOnlyList<(string?, bool)> SingleRecordListeners = new List<(string?, bool)> { ("new-event", false) };

    [Fact]
    public async Task SingleRecordListeners_GetOneOutboxInsertForTheWholePage()
    {
        var (interceptor, repo, table, fields) = Create(SingleRecordListeners);
        var items = new List<PipelineOutboxItem>();
        await repo.CreateOutboxItemsAsync(Arg.Do<IReadOnlyList<PipelineOutboxItem>>(items.AddRange), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());

        await interceptor.InterceptAddedManyAsync(table, fields, Records(5), CancellationToken.None);

        await repo.Received(1).CreateOutboxItemsAsync(Arg.Any<IReadOnlyList<PipelineOutboxItem>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        Assert.Equal(5, items.Count);
    }

    [Fact]
    public async Task TheBatchedEvents_AreTheSameEventsOneByOneInterceptionWouldHaveWritten()
    {
        var (batched, batchedRepo, table, fields) = Create(SingleRecordListeners);
        var batchedItems = new List<PipelineOutboxItem>();
        await batchedRepo.CreateOutboxItemsAsync(Arg.Do<IReadOnlyList<PipelineOutboxItem>>(batchedItems.AddRange), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        var (single, singleRepo, singleTable, _) = Create(null);   // null: the listeners cannot be read, so one by one
        var singleItems = new List<PipelineOutboxItem>();
        singleRepo.CreateOutboxItemAsync(Arg.Do<PipelineOutboxItem>(singleItems.Add), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        var records = Records(4);

        await batched.InterceptAddedManyAsync(table, fields, records, CancellationToken.None);
        await single.InterceptAddedManyAsync(singleTable, fields, records, CancellationToken.None);

        Assert.Equal(singleItems.Count, batchedItems.Count);
        for (var i = 0; i < records.Count; i++)
        {
            var b = batchedItems[i]; var s = singleItems[i];
            Assert.Equal(s.PipelineId, b.PipelineId);
            Assert.Equal(s.TriggerEvent, b.TriggerEvent);
            Assert.Equal(s.PayloadVersion, b.PayloadVersion);
            Assert.Equal(s.Published, b.Published);
            Assert.Equal(s.Depth, b.Depth);
            using var bp = JsonDocument.Parse(b.TriggerPayloadJson);
            using var sp = JsonDocument.Parse(s.TriggerPayloadJson);
            Assert.Equal(records[i].RecordPublicId.ToString(), bp.RootElement.GetProperty("RecordPublicId").GetString());
            Assert.Equal("Added", bp.RootElement.GetProperty("EventType").GetString());
            Assert.Equal("v" + i, bp.RootElement.GetProperty("NewValues").GetProperty("fid_10").GetString());
            // Same payload shape: every key a one-by-one event carries, a batched one carries.
            Assert.Equal(sp.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList(), bp.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList());
        }
        Assert.Equal(records.Count, batchedItems.Select(i => i.MessageId).Distinct().Count());   // each record keeps its own message id
        Assert.All(batchedItems, i => Assert.Equal(batchedItems[0].BatchId, i.BatchId));          // one page = one batch
    }

    [Fact]
    public async Task ABulkEventListener_KeepsEveryRecordOnItsOwn()
    {
        var (interceptor, repo, table, fields) = Create(new List<(string?, bool)> { ("new-bulk-event", false) }, stepSubtype: "new-bulk-event");

        await interceptor.InterceptAddedManyAsync(table, fields, Records(5), CancellationToken.None);

        // Each record raises its own event (a bulk event of one), as when records are created one at a time.
        await repo.Received(5).CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().CreateOutboxItemsAsync(Arg.Any<IReadOnlyList<PipelineOutboxItem>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARecordCountLimit_IsJudgedPerRecordNotPerPage()
    {
        // "Run only when at most 1 record was added": a page of 5 must not suppress the 5 single-record events.
        var (interceptor, repo, table, fields) = Create(new List<(string?, bool)> { ("new-event", true) }, limited: true, max: 1);

        await interceptor.InterceptAddedManyAsync(table, fields, Records(5), CancellationToken.None);

        await repo.Received(5).CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().CreateOutboxItemsAsync(Arg.Any<IReadOnlyList<PipelineOutboxItem>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenTheListenersCannotBeRead_EveryRecordIsInterceptedOnItsOwn()
    {
        var (interceptor, repo, table, fields) = Create(null);

        await interceptor.InterceptAddedManyAsync(table, fields, Records(3), CancellationToken.None);

        await repo.Received(3).CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASingleRecord_IsInterceptedTheOrdinaryWay()
    {
        var (interceptor, repo, table, fields) = Create(SingleRecordListeners);

        await interceptor.InterceptAddedManyAsync(table, fields, Records(1), CancellationToken.None);

        await repo.Received(1).CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().CreateOutboxItemsAsync(Arg.Any<IReadOnlyList<PipelineOutboxItem>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoRecords_WritesNothing()
    {
        var (interceptor, repo, table, fields) = Create(SingleRecordListeners);

        await interceptor.InterceptAddedManyAsync(table, fields, Records(0), CancellationToken.None);

        await repo.DidNotReceiveWithAnyArgs().CreateOutboxItemsAsync(default!, default, default);
        await repo.DidNotReceiveWithAnyArgs().CreateOutboxItemAsync(default!, default, default);
    }

    [Fact]
    public async Task NoActiveListeners_WritesNothing()
    {
        var (interceptor, repo, table, fields) = Create(new List<(string?, bool)>());
        repo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline>());

        await interceptor.InterceptAddedManyAsync(table, fields, Records(5), CancellationToken.None);

        await repo.DidNotReceiveWithAnyArgs().CreateOutboxItemsAsync(default!, default, default);
        await repo.DidNotReceiveWithAnyArgs().CreateOutboxItemAsync(default!, default, default);
    }

    [Fact]
    public async Task EveryOtherCaller_StillWritesOneOutboxItemPerEvent()
    {
        // InterceptBulkAsync (imports, mass update, ...) must be untouched by the Copy Records batching.
        var (interceptor, repo, table, fields) = Create(SingleRecordListeners);
        var changes = Records(3).Select(r => new PipelineRecordChange(r.RecordPublicId, new Dictionary<long, object?>(), r.Values, new List<long>(), PipelineRecordEventType.Added)).ToList();

        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1, CancellationToken.None);

        await repo.Received(3).CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().CreateOutboxItemsAsync(Arg.Any<IReadOnlyList<PipelineOutboxItem>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }
}
