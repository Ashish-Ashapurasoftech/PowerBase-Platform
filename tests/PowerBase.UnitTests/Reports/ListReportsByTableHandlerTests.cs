using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.ListReportsByTable;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Reports;

public class ListReportsByTableHandlerTests
{
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();

    private ListReportsByTableQueryHandler CreateSut() => new(_reportRepo);

    private static Report MakeReport(string name = "My Report", bool isDefault = false) => new()
    {
        Id = Random.Shared.NextInt64(1, 1000),
        PublicId = Guid.NewGuid(),
        Name = name,
        ReportType = "Table",
        Visibility = "Personal",
        IsDefault = isDefault,
        Definition = JsonSerializer.Serialize(new ReportDefinition { Columns = [1L, 2L] }),
        CreatedOn = DateTime.UtcNow,
    };

    [Fact]
    public async Task ListByTable_ReturnsAllReportsForTable()
    {
        var tableId = Guid.NewGuid();
        _reportRepo.ListByTableAsync(tableId, Arg.Any<CancellationToken>())
            .Returns(new List<Report> { MakeReport("Report A"), MakeReport("Report B") });
        var sut = CreateSut();

        var result = await sut.HandleAsync(new ListReportsByTableQuery(tableId));

        result.Should().HaveCount(2);
        result.Select(r => r.Name).Should().BeEquivalentTo(new[] { "Report A", "Report B" });
    }

    [Fact]
    public async Task ListByTable_EmptyTable_ReturnsEmptyList()
    {
        var tableId = Guid.NewGuid();
        _reportRepo.ListByTableAsync(tableId, Arg.Any<CancellationToken>())
            .Returns(new List<Report>());
        var sut = CreateSut();

        var result = await sut.HandleAsync(new ListReportsByTableQuery(tableId));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListByTable_DeserializesDefinitionColumns()
    {
        var tableId = Guid.NewGuid();
        _reportRepo.ListByTableAsync(tableId, Arg.Any<CancellationToken>())
            .Returns(new List<Report> { MakeReport() });
        var sut = CreateSut();

        var result = await sut.HandleAsync(new ListReportsByTableQuery(tableId));

        result.Single().Definition.Columns.Should().BeEquivalentTo(new[] { 1L, 2L });
    }

    [Fact]
    public async Task ListByTable_MapsIsDefaultFlag()
    {
        var tableId = Guid.NewGuid();
        _reportRepo.ListByTableAsync(tableId, Arg.Any<CancellationToken>())
            .Returns(new List<Report>
            {
                MakeReport("Default Report", isDefault: true),
                MakeReport("Other Report",   isDefault: false),
            });
        var sut = CreateSut();

        var result = await sut.HandleAsync(new ListReportsByTableQuery(tableId));

        result.Single(r => r.Name == "Default Report").IsDefault.Should().BeTrue();
        result.Single(r => r.Name == "Other Report").IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task ListByTable_PassesCorrectTablePublicIdToRepo()
    {
        var tableId = Guid.NewGuid();
        _reportRepo.ListByTableAsync(tableId, Arg.Any<CancellationToken>())
            .Returns(new List<Report>());
        var sut = CreateSut();

        await sut.HandleAsync(new ListReportsByTableQuery(tableId));

        await _reportRepo.Received(1).ListByTableAsync(tableId, Arg.Any<CancellationToken>());
    }
}
