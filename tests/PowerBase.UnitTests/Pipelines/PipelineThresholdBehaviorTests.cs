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

public class PipelineThresholdBehaviorTests
{
    [Theory]
    [InlineData("new-event", true, 5, 1, 1, 1)]
    [InlineData("new-event", true, 5, 5, 5, 5)]
    [InlineData("new-event", true, 5, 6, 6, 0)]
    [InlineData("new-event", true, 5, 6, 2, 0)]
    [InlineData("new-bulk-event", true, 5, 4, 4, 0)]
    [InlineData("new-bulk-event", true, 5, 5, 5, 1)]
    [InlineData("new-bulk-event", true, 5, 6, 6, 1)]
    [InlineData("new-bulk-event", true, 5, 6, 2, 1)]
    [InlineData("new-bulk-event", true, 5, 6, 0, 0)]
    [InlineData("new-event", false, 5, 6, 6, 6)]
    [InlineData("new-bulk-event", false, 5, 1, 1, 1)]
    public async Task ThresholdSelectsBatchAndFiltersSelectOutput(string subtype, bool limited, int threshold,
        int batchSize, int matches, int expectedRuns)
    {
        var (interceptor, repo, table, fields) = Create(subtype, limited, threshold);
        var items = new List<PipelineOutboxItem>();
        repo.CreateOutboxItemAsync(Arg.Do<PipelineOutboxItem>(items.Add), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        await interceptor.InterceptBulkAsync(table, fields, Changes(batchSize, matches), Guid.NewGuid(), Guid.NewGuid(), 1, CancellationToken.None);
        Assert.Equal(expectedRuns, items.Count);
        if (subtype == "new-bulk-event" && expectedRuns > 0)
        {
            using var payload = JsonDocument.Parse(items.Single().TriggerPayloadJson);
            Assert.Equal(matches, payload.RootElement.GetProperty("Count").GetInt32());
        }
    }

    [Fact]
    public async Task SeparateEditsDoNotAccumulateToMinimum()
    {
        var (interceptor, repo, table, fields) = Create("new-bulk-event", true, 5);
        for (var i = 0; i < 5; i++)
            await interceptor.InterceptBulkAsync(table, fields, Changes(1, 1), Guid.NewGuid(), Guid.NewGuid(), 1, CancellationToken.None);
        await repo.DidNotReceive().CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    private static List<PipelineRecordChange> Changes(int count, int matches) => Enumerable.Range(0, count)
        .Select(i => new PipelineRecordChange(Guid.NewGuid(), new Dictionary<long, object?>(),
            new Dictionary<long, object?> { [10] = i < matches ? "Yes" : "No" }, new List<long>(), PipelineRecordEventType.Added)).ToList();

    private static (PipelineTriggerInterceptor, IPipelineRepository, AppTable, List<AppField>) Create(string subtype, bool limited, int threshold)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new() { Id = 10, Fid = 10, Name = "Selected", TypeCode = "Text" } };
        repo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { new() { Id = 101, IsActive = true } });
        repo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> {
            new() { Type = "trigger", Subtype = subtype, ConfigJson = JsonSerializer.Serialize(new {
                TablePublicId = table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnAnyField = true,
                LimitRecords = limited, MaxRecords = threshold,
                Filters = new[] { new { Field = "fid_10", Operator = "is", Value = "Yes" } }
            }) }
        });
        return (new PipelineTriggerInterceptor(repo, Substitute.For<IRecordRepository>(), Substitute.For<IQueryContext>(),
            uow, Substitute.For<ILogger<PipelineTriggerInterceptor>>()), repo, table, fields);
    }
}
