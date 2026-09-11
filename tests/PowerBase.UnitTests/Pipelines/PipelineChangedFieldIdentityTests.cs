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

public class PipelineChangedFieldIdentityTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task MonitoredFieldsUseOneIdentityNamespace(bool bulk, bool changeMonitored, bool anyField)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        var context = Substitute.For<IQueryContext>();
        context.IsPipelineExecution.Returns(true);
        context.PipelineDepth.Returns(1);
        context.PipelineChainJson.Returns("[101]");
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        // Result FID 7 collides with Test Status metadata ID 7.
        var fields = new List<AppField> {
            new() { Id = 7, Fid = 6, Name = "Test Status", TypeCode = "Text" },
            new() { Id = 100, Fid = 7, Name = "Result", TypeCode = "Text" }
        };
        repo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { new() { Id = 101, IsActive = true } });
        repo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> {
            new() { Type = "trigger", Subtype = bulk ? "new-bulk-event" : "new-event", ConfigJson = JsonSerializer.Serialize(new {
                TablePublicId = table.PublicId.ToString(), TriggerOnModified = true,
                TriggerOnAnyField = anyField, TriggerFields = new[] { "fid_6" }
            }) }
        });
        var items = new List<PipelineOutboxItem>();
        repo.CreateOutboxItemAsync(Arg.Do<PipelineOutboxItem>(items.Add), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        var interceptor = new PipelineTriggerInterceptor(repo, Substitute.For<IRecordRepository>(), context, uow,
            Substitute.For<ILogger<PipelineTriggerInterceptor>>());
        var changed = fields[changeMonitored ? 0 : 1];
        if (bulk)
            await interceptor.InterceptBulkAsync(table, fields, new[] {
                new PipelineRecordChange(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?>(),
                    new[] { changed.Id }, PipelineRecordEventType.Modified)
            }, Guid.NewGuid(), Guid.NewGuid(), 1, CancellationToken.None);
        else
            await interceptor.InterceptAsync(table, fields, Guid.NewGuid(), new Dictionary<long, object?>(), "record-updated",
                CancellationToken.None, new Dictionary<long, object?>(), new long[] { changed.Fid!.Value });
        Assert.Equal(changeMonitored || anyField ? 1 : 0, items.Count);
        if (items.Count > 0) Assert.Equal("[101,101]", items[0].PipelineChain);
        if (!bulk && items.Count > 0)
        {
            using var payload = JsonDocument.Parse(items[0].TriggerPayloadJson);
            Assert.Equal("fid_" + changed.Fid, payload.RootElement.GetProperty("ChangedFieldFids")[0].GetString());
        }
    }
}
