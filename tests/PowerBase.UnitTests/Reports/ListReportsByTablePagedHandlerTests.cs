using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Reports.Queries.ListReportsByTablePaged;

namespace PowerBase.UnitTests.Reports;

public class ListReportsByTablePagedHandlerTests
{
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();

    private ListReportsByTablePagedQueryHandler CreateSut() => new(_reportRepo);

    private static ReportListItemDto MakeReport(string name = "My Report", bool isDefault = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ReportType = "Table",
        Visibility = "Shared",
        IsDefault = isDefault,
        CreatedOn = DateTime.UtcNow,
    };

    [Fact]
    public async Task HandleAsync_DefaultParams_UsesPage1PageSize20SortByName()
    {
        var tableId = Guid.NewGuid();
        _reportRepo.ListByTablePagedAsync(tableId, 1, 20, null, "name", false, Arg.Any<CancellationToken>())
            .Returns(new List<ReportListItemDto> { MakeReport() });
        _reportRepo.CountByTableAsync(tableId, null, Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        var result = await sut.HandleAsync(new ListReportsByTablePagedQuery(tableId));

        result.Items.Should().HaveCount(1);
        result.Total.Should().Be(1);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task HandleAsync_UnknownSortBy_FallsBackToName()
    {
        var tableId = Guid.NewGuid();
        _reportRepo.ListByTablePagedAsync(tableId, 1, 20, null, "name", false, Arg.Any<CancellationToken>())
            .Returns(new List<ReportListItemDto>());
        _reportRepo.CountByTableAsync(tableId, null, Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateSut();

        await sut.HandleAsync(new ListReportsByTablePagedQuery(tableId, SortBy: "notARealColumn"));

        await _reportRepo.Received(1).ListByTablePagedAsync(tableId, 1, 20, null, "name", false, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task HandleAsync_PageBelowOne_NormalizesToOne(int inputPage, int expectedPage)
    {
        var tableId = Guid.NewGuid();
        _reportRepo.ListByTablePagedAsync(tableId, expectedPage, 20, null, "name", false, Arg.Any<CancellationToken>())
            .Returns(new List<ReportListItemDto>());
        _reportRepo.CountByTableAsync(tableId, null, Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateSut();

        var result = await sut.HandleAsync(new ListReportsByTablePagedQuery(tableId, Page: inputPage));

        result.Page.Should().Be(expectedPage);
    }

    [Fact]
    public async Task HandleAsync_PassesSearchAndSortThrough()
    {
        var tableId = Guid.NewGuid();
        _reportRepo.ListByTablePagedAsync(tableId, 1, 20, "invoice", "createdOn", true, Arg.Any<CancellationToken>())
            .Returns(new List<ReportListItemDto> { MakeReport("Invoice Report") });
        _reportRepo.CountByTableAsync(tableId, "invoice", Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        var result = await sut.HandleAsync(new ListReportsByTablePagedQuery(tableId, Search: "invoice", SortBy: "createdOn", SortDesc: true));

        result.Items.Should().ContainSingle(r => r.Name == "Invoice Report");
    }
}
