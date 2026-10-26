using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// A record step's search-index update is queued only after its transaction commits: the repository hands the message to the
/// step instead of publishing it inside the transaction, so a rolled-back write never reaches the search index.
/// </summary>
public class PipelineIndexPublishAfterCommitTests
{
    [Fact]
    public async Task CreateRecordStep_QueuesTheIndexUpdate_AfterTheCommit()
    {
        var h = new Harness();

        await h.RunCreateRecordAsync();

        Assert.Equal(new[] { "insert", "commit", "publish" }, h.Order);
        Assert.Single(h.Published);
        Assert.Equal(h.CreatedPublicId, h.Published[0].RecordPublicId);
    }

    [Fact]
    public async Task CreateRecordStep_RolledBack_NeverQueuesTheIndexUpdate()
    {
        var h = new Harness();
        h.Receipts.InsertAsync(Arg.Any<PipelineStepIdempotencyLog>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("receipt write failed")));

        try { await h.RunCreateRecordAsync(); }
        catch (Exception) { /* the step's failure is what this test causes */ }

        Assert.DoesNotContain("commit", h.Order);
        Assert.Contains("rollback", h.Order);
        Assert.Empty(h.Published);
    }

    private sealed class Harness
    {
        public readonly List<string> Order = new();
        public readonly List<SearchIndexMessage> Published = new();
        public readonly IPipelineStepIdempotencyRepository Receipts = Substitute.For<IPipelineStepIdempotencyRepository>();
        public readonly Guid CreatedPublicId = Guid.NewGuid();
        private readonly Guid _tableId = Guid.NewGuid();

        public async Task RunCreateRecordAsync()
        {
            var field = new AppField { Id = 106, Fid = 6, Name = "Value", TypeCode = "Text" };
            var services = new ServiceCollection();
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new PipelineExecutionOptions()));
            var pipelines = Substitute.For<IPipelineRepository>();
            pipelines.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true });
            services.AddSingleton(pipelines);
            services.AddSingleton<PipelineLoopWorkerPool>();
            services.AddScoped<IPipelineEngine, PipelineEngine>();
            services.AddScoped<IQueryContext>(_ =>
            {
                var query = Substitute.For<IQueryContext>();
                query.TenantId.Returns(42L);
                query.UserId.Returns(7L);
                return query;
            });

            var uow = Substitute.For<ITenantUnitOfWork>();
            uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ => { Order.Add("commit"); return Task.CompletedTask; });
            uow.RollbackAsync(Arg.Any<CancellationToken>()).Returns(_ => { Order.Add("rollback"); return Task.CompletedTask; });
            services.AddScoped(_ => uow);

            var records = Substitute.For<IRecordRepository>();
            records.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(),
                    Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>?>())
                .Returns(call =>
                {
                    Order.Add("insert");
                    // The repository publishes on its own when no callback is given: a step that passes none publishes inside its transaction.
                    var onIndexMessage = call.ArgAt<Action<SearchIndexMessage>?>(5)
                        ?? throw new Xunit.Sdk.XunitException("The step let the repository publish the index update inside its transaction.");
                    onIndexMessage(new SearchIndexMessage { Action = IndexAction.Upsert, TenantId = 42, TableId = 1, RecordPublicId = CreatedPublicId });
                    return CreatedPublicId;
                });
            records.GetBulkUpsertRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>>(new Dictionary<long, IReadOnlyDictionary<string, object?>>()));
            services.AddSingleton(records);

            var publisher = Substitute.For<IMessagePublisher>();
            publisher.PublishBatchAsync(Arg.Any<IEnumerable<SearchIndexMessage>>(), Arg.Any<CancellationToken>())
                .Returns(call => { Order.Add("publish"); Published.AddRange(call.ArgAt<IEnumerable<SearchIndexMessage>>(0)); return Task.CompletedTask; });
            // A single-record step queues its one message on its own.
            publisher.PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>())
                .Returns(call => { Order.Add("publish"); Published.Add(call.ArgAt<SearchIndexMessage>(0)); return Task.CompletedTask; });
            services.AddSingleton(publisher);

            var tables = Substitute.For<IAppTableRepository>();
            tables.GetByPublicIdAsync(_tableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 1, PublicId = _tableId, Name = "F2" });
            services.AddSingleton(tables);
            var fields = Substitute.For<IAppFieldRepository>();
            fields.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(new List<AppField> { field });
            services.AddSingleton(fields);
            services.AddSingleton(Receipts);
            services.AddSingleton(Substitute.For<IRecordWriteService>());
            services.AddSingleton(Substitute.For<IRelationshipRepository>());
            services.AddSingleton(Substitute.For<IEmailService>());
            services.AddSingleton(Substitute.For<IHttpClientFactory>());
            services.AddSingleton(Substitute.For<IFileStorageService>());
            services.AddSingleton(Substitute.For<ILogger<PipelineEngine>>());
            services.AddSingleton(Substitute.For<IPipelineTriggerInterceptor>());
            services.AddSingleton(Substitute.For<IPipelineAuditFormatter>());
            services.AddSingleton(Substitute.For<IAdminRepository>());
            services.AddSingleton(Substitute.For<ITenantRepository>());
            services.AddSingleton(Substitute.For<IPipelineRecordSearchService>());
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IPipelineEngine>();

            var create = new PipelineStep
            {
                Id = 3, PipelineId = 1, PublicId = Guid.NewGuid(), RefId = "c", Type = "action", Subtype = "create-record",
                ConfigJson = JsonSerializer.Serialize(new { TableId = _tableId, FieldMappings = new[] { new { Field = "fid_6", Value = "Ronak" } } })
            };
            var steps = new Dictionary<string, object>();
            var context = new Dictionary<string, object> { ["steps"] = steps, ["_MessageId"] = Guid.NewGuid(), ["_CreatedBy"] = 7L };
            await (Task)typeof(PipelineEngine).GetMethod("ExecuteSiblingStepsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(engine, new object?[] { 1L, new List<PipelineStep> { create }, null, null, context, steps,
                    new List<PipelineEngine.RawStepAuditSnapshot>(), "root", CancellationToken.None })!;
            // The publish is fire-and-forget after the commit; the substitute runs it synchronously.
        }
    }
}
