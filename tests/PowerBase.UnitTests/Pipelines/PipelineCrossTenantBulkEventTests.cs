using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Pipelines;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// An On New Bulk Event flow runs, and reads its staged records, in the OWNER tenant's database. When the
/// source table lives in another tenant the staging rows must therefore be written to the owner tenant
/// (before the job is queued), not into the tenant where the records changed.
/// </summary>
public class PipelineCrossTenantBulkEventTests
{
    private sealed class CapturingInterceptor : PipelineTriggerInterceptor
    {
        public List<(long TenantId, int Count)> Staged { get; } = new();
        public List<string> Order { get; } = new();

        public CapturingInterceptor(IPipelineRepository repo, IQueryContext context, ITenantUnitOfWork uow,
            IMainPipelineQueueRepository queue, ITenantConnectionResolver? resolver)
            : base(repo, Substitute.For<IRecordRepository>(), context, uow, null!, queue,
                Substitute.For<ILogger<PipelineTriggerInterceptor>>(), null, resolver)
        {
            queue.When(q => q.EnqueueAsync(Arg.Any<PipelineQueue>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>()))
                .Do(_ => Order.Add("enqueue"));
        }

        protected override Task InsertStagingIntoTenantAsync(long tenantId, List<PipelineBulkEventRecord> records, CancellationToken ct)
        {
            Staged.Add((tenantId, records.Count));
            Order.Add("stage");
            return Task.CompletedTask;
        }
    }

    private readonly AppTable _table = new() { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
    private readonly List<AppField> _fields = new() { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IMainPipelineQueueRepository _queue = Substitute.For<IMainPipelineQueueRepository>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();

    public PipelineCrossTenantBulkEventTests()
    {
        _uow.Transaction.Returns(Substitute.For<System.Data.IDbTransaction>());
        _pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { new() { Id = 101, IsActive = true } });
        _pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
        {
            new()
            {
                Type = "trigger", Subtype = "new-bulk-event",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = _table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnAnyField = true })
            }
        });
    }

    /// <summary>The subscription is built from the current tenant for owner and target (first two reads);
    /// the later "is this the same tenant" read then reports the record changed in another tenant.</summary>
    private static IQueryContext ContextInAnotherTenant()
    {
        var context = Substitute.For<IQueryContext>();
        var reads = 0;
        context.TenantId.Returns(_ => reads++ < 2 ? 6L : 8L);
        return context;
    }

    private static List<PipelineRecordChange> Changes() => new()
    {
        new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Alice" }, new List<long>(), PipelineRecordEventType.Added),
        new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Bob" }, new List<long>(), PipelineRecordEventType.Added)
    };

    [Fact]
    public async Task CrossTenantBulkEvent_StagesRecordsInTheOwnerTenant_BeforeQueueing()
    {
        var interceptor = new CapturingInterceptor(_pipelineRepo, ContextInAnotherTenant(), _uow, _queue, Substitute.For<ITenantConnectionResolver>());

        await interceptor.InterceptBulkAsync(_table, _fields, Changes(), Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        interceptor.Staged.Should().ContainSingle().Which.Should().Be((6L, 2));
        interceptor.Order.Should().Equal("stage", "enqueue");
        await _pipelineRepo.DidNotReceive().InsertBulkEventRecordsAsync(Arg.Any<List<PipelineBulkEventRecord>>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
        await _queue.Received(1).EnqueueAsync(Arg.Is<PipelineQueue>(job => job.TenantId == 6L && job.TriggerEvent == "new-bulk-event"), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SameTenantBulkEvent_StillStagesInTheCurrentTransaction()
    {
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(6L);
        var interceptor = new CapturingInterceptor(_pipelineRepo, context, _uow, _queue, Substitute.For<ITenantConnectionResolver>());

        await interceptor.InterceptBulkAsync(_table, _fields, Changes(), Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        interceptor.Staged.Should().BeEmpty();
        await _pipelineRepo.Received(1).InsertBulkEventRecordsAsync(
            Arg.Is<List<PipelineBulkEventRecord>>(records => records.Count == 2), _uow.Transaction, Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), _uow.Transaction, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrossTenantBulkEvent_WithoutTenantResolver_KeepsTheOldBehaviour()
    {
        var interceptor = new CapturingInterceptor(_pipelineRepo, ContextInAnotherTenant(), _uow, _queue, resolver: null);

        await interceptor.InterceptBulkAsync(_table, _fields, Changes(), Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        interceptor.Staged.Should().BeEmpty();
        await _pipelineRepo.Received(1).InsertBulkEventRecordsAsync(Arg.Any<List<PipelineBulkEventRecord>>(), _uow.Transaction, Arg.Any<CancellationToken>());
    }
}
