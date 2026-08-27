using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.GetReportPreviewMetadata;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Reports;

public class GetReportPreviewMetadataQueryHandlerTests
{
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();
    private readonly PowerBase.Application.Relationships.IRelationalProjector _relationalProjector = Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>();
    private readonly IAzureSearchService _searchService = Substitute.For<IAzureSearchService>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    [Fact]
    public async Task WhenReportNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var reportId = Guid.NewGuid();
        _reportRepo.GetVisibleReportAsync(reportId, Arg.Any<CancellationToken>())
            .Returns((Report?)null);

        var runHandler = new RunReportQueryHandler(
            _reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer,
            _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext,
            Substitute.For<ILogger<RunReportQueryHandler>>());

        var handler = new GetReportPreviewMetadataQueryHandler(_reportRepo, _tableRepo, _fieldRepo, runHandler);

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(new GetReportPreviewMetadataQuery(reportId)));
    }

    [Fact]
    public async Task WhenReportExists_ReturnsMetadataWithoutRawRecords()
    {
        // Arrange
        var reportId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var table = new AppTable { Id = 1, PublicId = tableId, Name = "Customers", AppId = 1, PhysicalTableName = "t_1" };
        var fields = new List<AppField>
        {
            new() { Id = 10, Fid = 10, Name = "Full Name", TypeCode = "Text", AppTableId = 1, IsReportable = true },
            new() { Id = 20, Fid = 20, Name = "Revenue", TypeCode = "Currency", AppTableId = 1, IsReportable = true }
        };

        var definition = new ReportDefinition
        {
            Columns = new List<long> { 10, 20 },
            Aggregations = new List<SummaryAggregation>
            {
                new() { FieldId = 20, Function = "Sum" }
            }
        };

        var report = new Report
        {
            Id = 5,
            PublicId = reportId,
            AppTableId = 1,
            Name = "Revenue Summary",
            ReportType = "Table",
            Definition = JsonSerializer.Serialize(definition)
        };

        _reportRepo.GetVisibleReportAsync(reportId, Arg.Any<CancellationToken>()).Returns(report);
        _tableRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(fields);

        _enforcer.GetTableAccessAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = fields,
                ViewScope = RecordScopes.AllRecords
            });

        _recordRepo.ListAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Dictionary<string, object?>>());
        _recordRepo.CountAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(42);

        var runHandler = new RunReportQueryHandler(
            _reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer,
            _userRepo, _formulaProjector, _relationalProjector, _searchService, _appUserRepo, _queryContext,
            Substitute.For<ILogger<RunReportQueryHandler>>());

        var handler = new GetReportPreviewMetadataQueryHandler(_reportRepo, _tableRepo, _fieldRepo, runHandler);

        // Act
        var result = await handler.HandleAsync(new GetReportPreviewMetadataQuery(reportId));

        // Assert
        result.Should().NotBeNull();
        result.ReportId.Should().Be(reportId);
        result.ReportName.Should().Be("Revenue Summary");
        result.TotalCount.Should().Be(42);
        result.IsDataMasked.Should().BeTrue();
        result.Columns.Should().HaveCount(2);
        result.Aggregations.Should().HaveCount(1);
        result.Aggregations[0].Function.Should().Be("Sum");
    }
}
