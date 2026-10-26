using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Queries.SearchGlobalRecords;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Records;

public class SearchGlobalRecordsAuthorizationTests
{
    private readonly IAzureSearchService _searchService = Substitute.For<IAzureSearchService>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();

    private readonly SearchGlobalRecordsQueryHandler _sut;

    public SearchGlobalRecordsAuthorizationTests()
    {
        _queryContext.TenantId.Returns(100L);
        _sut = new SearchGlobalRecordsQueryHandler(
            _searchService,
            _queryContext,
            _appRepo,
            _tableRepo,
            _fieldRepo,
            _recordRepo,
            _enforcer);
    }

    [Fact]
    public async Task HandleAsync_OmitRecordsFromTablesUserCannotView()
    {
        // Arrange
        var recordPublicId = Guid.NewGuid();
        var rawResult = new GlobalSearchResult(
            PublicId: recordPublicId,
            AppId: 10L,
            TableId: 20L,
            Fields: new Dictionary<string, string> { { "f_7", "Confidential Value" } });

        _searchService.SearchGlobalAsync(100L, "query", null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<GlobalSearchResult> { rawResult }, 1L));

        var app = new App { Id = 10L, PublicId = Guid.NewGuid(), Name = "App1" };
        var table = new AppTable { Id = 20L, AppId = 10L, PublicId = Guid.NewGuid(), Name = "RestrictedTable" };
        var fields = new List<AppField> { new() { Id = 1L, Fid = 7, Name = "Secret", AppTableId = 20L } };

        _tableRepo.GetByIdAsync(20L, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(20L, Arg.Any<CancellationToken>()).Returns(fields);
        _appRepo.GetByIdAsync(10L, Arg.Any<CancellationToken>()).Returns(app);

        // Deny view access
        _enforcer.GetTableAccessAsync(table, fields, Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = false, ViewScope = RecordScopes.None });

        // Act
        var result = await _sut.HandleAsync(new SearchGlobalRecordsQuery("query", null, 1, 50));

        // Assert
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_OmitRecordsFailingRowLevelViewFilterOrOwnership()
    {
        // Arrange
        var recordPublicId = Guid.NewGuid();
        var rawResult = new GlobalSearchResult(
            PublicId: recordPublicId,
            AppId: 10L,
            TableId: 20L,
            Fields: new Dictionary<string, string> { { "f_7", "Some Value" } });

        _searchService.SearchGlobalAsync(100L, "query", null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<GlobalSearchResult> { rawResult }, 1L));

        var app = new App { Id = 10L, PublicId = Guid.NewGuid(), Name = "App1" };
        var table = new AppTable { Id = 20L, AppId = 10L, PublicId = Guid.NewGuid(), Name = "Table1" };
        var fields = new List<AppField> { new() { Id = 1L, Fid = 7, Name = "Name", AppTableId = 20L } };

        _tableRepo.GetByIdAsync(20L, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(20L, Arg.Any<CancellationToken>()).Returns(fields);
        _appRepo.GetByIdAsync(10L, Arg.Any<CancellationToken>()).Returns(app);

        // Restrict to OwnRecords
        var access = new TableAccessContext
        {
            Unrestricted = false,
            ViewScope = RecordScopes.OwnRecords,
            RestrictToCreatedBy = 42L,
            VisibleFields = fields
        };
        _enforcer.GetTableAccessAsync(table, fields, Arg.Any<CancellationToken>()).Returns(access);

        // Record does NOT belong to user 42
        _recordRepo.ExistsWithViewFilterAsync(table, fields, recordPublicId, null, 42L, Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _sut.HandleAsync(new SearchGlobalRecordsQuery("query", null, 1, 50));

        // Assert
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_NeverDisplaysHiddenOrUnpermittedFields()
    {
        // Arrange
        var recordPublicId = Guid.NewGuid();
        var rawResult = new GlobalSearchResult(
            PublicId: recordPublicId,
            AppId: 10L,
            TableId: 20L,
            Fields: new Dictionary<string, string>
            {
                { "f_99", "Sensitive Medical Diagnosis" } // Not in VisibleFields!
            });

        _searchService.SearchGlobalAsync(100L, "query", null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<GlobalSearchResult> { rawResult }, 1L));

        var app = new App { Id = 10L, PublicId = Guid.NewGuid(), Name = "HealthApp" };
        var table = new AppTable { Id = 20L, AppId = 10L, PublicId = Guid.NewGuid(), Name = "Patients", SingularLabel = "Patient" };
        var sensitiveField = new AppField { Id = 1L, Fid = 99, Name = "Diagnosis", AppTableId = 20L };
        var nameField = new AppField { Id = 2L, Fid = 7, Name = "PatientName", AppTableId = 20L };
        var allFields = new List<AppField> { sensitiveField, nameField };

        _tableRepo.GetByIdAsync(20L, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(20L, Arg.Any<CancellationToken>()).Returns(allFields);
        _appRepo.GetByIdAsync(10L, Arg.Any<CancellationToken>()).Returns(app);

        // User can only see nameField (sensitiveField fid 99 is HIDDEN from this role)
        var access = new TableAccessContext
        {
            Unrestricted = false,
            ViewScope = RecordScopes.AllRecords,
            VisibleFields = new List<AppField> { nameField }
        };
        _enforcer.GetTableAccessAsync(table, allFields, Arg.Any<CancellationToken>()).Returns(access);

        // Act
        var result = await _sut.HandleAsync(new SearchGlobalRecordsQuery("query", null, 1, 50));

        // Assert
        result.Items.Should().HaveCount(1);
        // Sensitive Diagnosis must NOT be displayed!
        result.Items[0].PrimaryText.Should().NotContain("Sensitive Medical Diagnosis");
        result.Items[0].PrimaryText.Should().Be($"Record ID: {recordPublicId.ToString()[..8]}");
    }

    [Fact]
    public async Task HandleAsync_DisplaysAuthorizedFieldWhenPermitted()
    {
        // Arrange
        var recordPublicId = Guid.NewGuid();
        var rawResult = new GlobalSearchResult(
            PublicId: recordPublicId,
            AppId: 10L,
            TableId: 20L,
            Fields: new Dictionary<string, string>
            {
                { "f_7", "Alice Wonderland" }
            });

        _searchService.SearchGlobalAsync(100L, "query", null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<GlobalSearchResult> { rawResult }, 1L));

        var app = new App { Id = 10L, PublicId = Guid.NewGuid(), Name = "App" };
        var displayField = new AppField { Id = 1L, Fid = 7, Name = "FullName", AppTableId = 20L };
        var table = new AppTable { Id = 20L, AppId = 10L, PublicId = Guid.NewGuid(), Name = "Users", DisplayFieldId = 7L };
        var allFields = new List<AppField> { displayField };

        _tableRepo.GetByIdAsync(20L, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(20L, Arg.Any<CancellationToken>()).Returns(allFields);
        _appRepo.GetByIdAsync(10L, Arg.Any<CancellationToken>()).Returns(app);

        var access = new TableAccessContext
        {
            Unrestricted = false,
            ViewScope = RecordScopes.AllRecords,
            VisibleFields = allFields
        };
        _enforcer.GetTableAccessAsync(table, allFields, Arg.Any<CancellationToken>()).Returns(access);

        // Act
        var result = await _sut.HandleAsync(new SearchGlobalRecordsQuery("query", null, 1, 50));

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].PrimaryText.Should().Be("Alice Wonderland");
    }
}
