using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineRecordSearchServiceTests
{
    // Stop at the database boundary: SQL construction must succeed before opening a connection.
    [Theory]
    [InlineData("search", false)]
    [InlineData("search", true)]
    [InlineData("pages", false)]
    [InlineData("pages", true)]
    [InlineData("copy", false)]
    [InlineData("copy", true)]
    public async Task Readers_BuildFiltersAndReachDatabase(string reader, bool withFilter)
    {
        var factory = Substitute.For<ITenantConnectionFactory>();
        var boundary = new InvalidOperationException("Database boundary reached");
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        factory.CreateAsync(ct).Returns(_ => Task.FromException<SqlConnection>(boundary));
        var service = new PipelineRecordSearchService(factory,
            Substitute.For<IQueryContext>(), Substitute.For<IEncryptionService>(),
            Options.Create(new PipelineExecutionOptions()));
        var table = new AppTable();
        AppField[] fields = [new() { Id = 1007, Fid = 7, Name = "Amount", TypeCode = "Number", PhysicalColumnName = "f_7" }];
        var filter = withFilter ? new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "gt", Value = "10" } }]
        } : null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (reader == "search")
                await service.SearchAsync(table, fields, filterTree: filter, ct: ct);
            else
            {
                var pages = reader == "pages"
                    ? service.SearchPagesAsync(table, fields, 25, filter, ct: ct)
                    : service.ReadCopySnapshotAsync(table, fields, filter, ct);
                await foreach (var _ in pages) { }
            }
        });

        Assert.Same(boundary, exception);
        await factory.Received(1).CreateAsync(ct);
    }
}
