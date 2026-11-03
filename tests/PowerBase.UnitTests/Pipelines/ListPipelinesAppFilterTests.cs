using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Queries.ListPipelines;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>The PowerFlows list's App filter: the chosen app reaches the list and the count; no app keeps the old request.</summary>
public class ListPipelinesAppFilterTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IQueryContext _context = Substitute.For<IQueryContext>();

    public ListPipelinesAppFilterTests()
    {
        _context.UserId.Returns(7L);
        _repo.ListByUserPagedAsync(default, default, default, default, default!, default, default, default, default)
            .ReturnsForAnyArgs(new List<PipelineListItemDetail>());
        _repo.CountByUserAsync(default, default, default, default, default).ReturnsForAnyArgs(0);
    }

    private ListPipelinesQueryHandler Sut() => new(_repo, _context);

    [Fact]
    public async Task Handle_WithAppFilter_PassesTheAppToListAndCount()
    {
        var app = Guid.NewGuid();

        await Sut().HandleAsync(new ListPipelinesQuery(Guid.NewGuid(), 2, 25, "abc", "name", false, true, app));

        await _repo.Received(1).ListByUserPagedAsync(7L, 2, 25, "abc", "name", false, true, Arg.Any<CancellationToken>(), app);
        await _repo.Received(1).CountByUserAsync(7L, "abc", true, Arg.Any<CancellationToken>(), app);
    }

    [Fact]
    public async Task Handle_WithoutAppFilter_ListsEveryAppAsBefore()
    {
        await Sut().HandleAsync(new ListPipelinesQuery(Guid.NewGuid()));

        await _repo.Received(1).ListByUserPagedAsync(7L, 1, 10, null, "createdOn", true, null, Arg.Any<CancellationToken>(), null);
        await _repo.Received(1).CountByUserAsync(7L, null, null, Arg.Any<CancellationToken>(), null);
    }

    [Fact]
    public async Task Handle_AppFilterTogetherWithStatusAndSearch_KeepsAllFilters()
    {
        var app = Guid.NewGuid();

        await Sut().HandleAsync(new ListPipelinesQuery(Guid.NewGuid(), Search: "flow", IsActive: false, FilterAppPublicId: app));

        await _repo.Received(1).ListByUserPagedAsync(7L, 1, 10, "flow", "createdOn", true, false, Arg.Any<CancellationToken>(), app);
    }

    [Fact]
    public void Repository_ListAndCountSql_FilterByTheAppOnlyWhenOneIsGiven()
    {
        var type = typeof(PowerBase.Infrastructure.Repositories.PipelineRepository);
        string Sql(string name) => (string)type.GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        const string filter = "(@appPublicId IS NULL OR p.AppId = (SELECT a.Id FROM meta.App a WHERE a.PublicId = @appPublicId))";

        Sql("ListByUserPagedSqlTemplate").Should().Contain(filter);
        Sql("CountByUserSql").Should().Contain(filter);
    }

    [Fact]
    public void Repository_ListAndCountSql_LeaveOutTheDeletedAppsPowerFlows()
    {
        var type = typeof(PowerBase.Infrastructure.Repositories.PipelineRepository);
        string Sql(string name) => (string)type.GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        const string liveApp = "EXISTS (SELECT 1 FROM meta.App live WHERE live.Id = p.AppId AND live.IsDeleted = 0)";

        Sql("ListByUserPagedSqlTemplate").Should().Contain(liveApp);
        Sql("CountByUserSql").Should().Contain(liveApp, "the total must match the rows the list shows");
    }
}
