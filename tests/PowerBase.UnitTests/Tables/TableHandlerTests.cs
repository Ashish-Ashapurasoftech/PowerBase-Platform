using FluentAssertions;
using PowerBase.Application.Relationships.Commands.DeleteRelationship;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tables.Commands.CreateTable;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Application.Tables.Queries.GetTable;
using PowerBase.Application.Tables.Queries.ListTables;
using PowerBase.Application.Tables.Queries.ListTableNavItems;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Tables;

public class TableHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IAppSeeder _appSeeder = Substitute.For<IAppSeeder>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();

    private static App MakeApp(long id = 10) => new() { Id = id, PublicId = Guid.NewGuid(), Name = "App" };

    private static AppTable MakeTable(long id = 20, long appId = 10) => new()
    {
        Id = id, PublicId = Guid.NewGuid(), AppId = appId, Name = "Contacts"
    };

    public TableHandlerTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
        _appSeeder.CreateTableWithDefaultsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var t = ci.Arg<AppTable>();
                t.Id = 20;
                t.PublicId = Guid.NewGuid();
                t.PhysicalTableName = PhysicalNaming.TableName(20L);
                return t;
            });
    }

    // --- CreateTableCommandHandler ---

    [Fact]
    public async Task CreateTable_ValidCommand_CallsSchemaEngineAndReturnsResult()
    {
        var app = MakeApp();
        _appRepo.GetByPublicIdAsync(app.PublicId).Returns(app);
        _tableRepo.NameExistsInAppAsync(app.Id, "Contacts").Returns(false);
        var sut = new CreateTableCommandHandler(_appRepo, _tableRepo, _queryContext, _auditRepo, _appSeeder);

        var result = await sut.HandleAsync(new CreateTableCommand(app.PublicId, "Contacts", null, null, null, null));

        result.Name.Should().Be("Contacts");
        result.PhysicalTableName.Should().Be(PhysicalNaming.TableName(20L));
        await _appSeeder.Received(1).CreateTableWithDefaultsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTable_DuplicateName_ThrowsDuplicateException()
    {
        var app = MakeApp();
        _appRepo.GetByPublicIdAsync(app.PublicId).Returns(app);
        _tableRepo.NameExistsInAppAsync(app.Id, "Contacts").Returns(true);
        var sut = new CreateTableCommandHandler(_appRepo, _tableRepo, _queryContext, _auditRepo, _appSeeder);

        await sut.Invoking(s => s.HandleAsync(new CreateTableCommand(app.PublicId, "Contacts", null, null, null, null)))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Fact]
    public async Task CreateTable_EmptyName_ThrowsValidationException()
    {
        var sut = new CreateTableCommandHandler(_appRepo, _tableRepo, _queryContext, _auditRepo, _appSeeder);

        await sut.Invoking(s => s.HandleAsync(new CreateTableCommand(Guid.NewGuid(), "", null, null, null, null)))
            .Should().ThrowAsync<ValidationException>();
    }

    // --- DeleteTableCommandHandler ---

    [Fact]
    public async Task DeleteTable_CallsDeleteOnRepo()
    {
        var id = Guid.NewGuid();
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(id).Returns(table);
        _relRepo.ListByTableAsync(table.Id).Returns(new List<Relationship>());
        
        var deleteRelHandler = new DeleteRelationshipCommandHandler(_tableRepo, _fieldRepo, _relRepo, _recordRepo, _auditRepo);
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var sut = new DeleteTableCommandHandler(_tableRepo, _fieldRepo, pipelineRepo, _auditRepo, _relRepo, deleteRelHandler);

        await sut.HandleAsync(new DeleteTableCommand(id));

        await _tableRepo.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    // --- GetTableQueryHandler ---

    [Fact]
    public async Task GetTable_ReturnsTableWithFields()
    {
        var table = MakeTable();
        var fields = new List<AppField> { new() { Id = 1, Name = "Name" } };
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(fields);
        var sut = new GetTableQueryHandler(_tableRepo, _fieldRepo);

        var result = await sut.HandleAsync(new GetTableQuery(table.PublicId));

        result.Table.Should().BeSameAs(table);
        result.Fields.Should().HaveCount(1);
        result.SummarySourceSettings.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTable_MinMaxSummary_CarriesSourceFieldSettings_OnlyForMinMax()
    {
        var table = MakeTable();
        const string dueDateSettings = "{\"showMonthName\":true}";
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 30, Name = "Latest Due", TypeCode = "Summary",
                    Settings = "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Max\",\"targetFid\":6}" },
            new() { Id = 2, Fid = 31, Name = "Tasks", TypeCode = "Summary",
                    Settings = "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Count\"}" },
            new() { Id = 3, Fid = 32, Name = "Titles", TypeCode = "Summary",
                    Settings = "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"CombinedText\",\"targetFid\":5}" },
            new() { Id = 4, Fid = 33, Name = "Earliest Due", TypeCode = "Summary",
                    Settings = "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Min\",\"targetFid\":6}" },
        };
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(fields);
        _fieldRepo.ListByTableAsync(77).Returns(new List<AppField>
        {
            new() { Id = 50, Fid = 5, Name = "Title", TypeCode = "Text", Settings = "{}" },
            new() { Id = 60, Fid = 6, Name = "Due Date", TypeCode = "Date", Settings = dueDateSettings },
        });
        var sut = new GetTableQueryHandler(_tableRepo, _fieldRepo);

        var result = await sut.HandleAsync(new GetTableQuery(table.PublicId));

        result.SummarySourceSettings.Should().BeEquivalentTo(new Dictionary<long, string?> { [1] = dueDateSettings, [4] = dueDateSettings });
        await _fieldRepo.Received(1).ListByTableAsync(77, Arg.Any<CancellationToken>());   // once per child table
    }

    [Fact]
    public async Task GetTable_LookupsAndSumAvgSummaries_CarryTheirSourceFieldSettings()
    {
        var table = MakeTable();
        const string amountSettings = "{\"decimals\":0,\"symbol\":\"₹\"}";
        const string budgetSettings = "{\"decimals\":2,\"separator\":\",\"}";
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 30, Name = "Total Amount", TypeCode = "Summary",
                    Settings = "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Sum\",\"targetFid\":6}" },
            new() { Id = 2, Fid = 31, Name = "Average Amount", TypeCode = "Summary",
                    Settings = "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Avg\",\"targetFid\":6}" },
            new() { Id = 3, Fid = 32, Name = "Budget", TypeCode = "Lookup",
                    Settings = "{\"sourceTableId\":88,\"sourceFid\":9,\"sourceTypeCode\":\"Currency\"}" },
            new() { Id = 4, Fid = 33, Name = "City", TypeCode = "Lookup",
                    Settings = "{\"sourceTableId\":88,\"sourceFid\":12,\"sourceTypeCode\":\"Address\",\"sourceSubField\":\"city\"}" },
        };
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(fields);
        _fieldRepo.ListByTableAsync(77).Returns(new List<AppField> { new() { Id = 60, Fid = 6, Name = "Amount", TypeCode = "Currency", Settings = amountSettings } });
        _fieldRepo.ListByTableAsync(88).Returns(new List<AppField>
        {
            new() { Id = 90, Fid = 9, Name = "Budget", TypeCode = "Currency", Settings = budgetSettings },
            new() { Id = 120, Fid = 12, Name = "Address", TypeCode = "Address", Settings = "{\"displayBold\":true}" },
        });
        var sut = new GetTableQueryHandler(_tableRepo, _fieldRepo);

        var result = await sut.HandleAsync(new GetTableQuery(table.PublicId));

        // An address part is plain text — the Address field's settings don't describe it.
        result.SummarySourceSettings.Should().BeEquivalentTo(new Dictionary<long, string?> { [1] = amountSettings, [2] = amountSettings, [3] = budgetSettings });
    }

    [Fact]
    public async Task GetTable_LookupOfALookup_LaysTheMiddleLookupsOwnDisplayOverItsSource()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField>
        {
            new() { Id = 1, Fid = 30, Name = "Client Budget", TypeCode = "Lookup", Settings = "{\"sourceTableId\":88,\"sourceFid\":9}" },
        });
        _fieldRepo.ListByTableAsync(88).Returns(new List<AppField>
        {
            new() { Id = 90, Fid = 9, Name = "Budget", TypeCode = "Lookup",
                    Settings = "{\"sourceTableId\":99,\"sourceFid\":4,\"display\":{\"decimals\":0,\"displayBold\":null}}" },
        });
        _fieldRepo.ListByTableAsync(99).Returns(new List<AppField>
        {
            new() { Id = 40, Fid = 4, Name = "Budget", TypeCode = "Currency", Settings = "{\"decimals\":2,\"symbol\":\"$\"}" },
        });
        var sut = new GetTableQueryHandler(_tableRepo, _fieldRepo);

        var result = await sut.HandleAsync(new GetTableQuery(table.PublicId));

        using var shown = System.Text.Json.JsonDocument.Parse(result.SummarySourceSettings[1]!);
        shown.RootElement.GetProperty("decimals").GetInt32().Should().Be(0);   // the middle lookup's own
        shown.RootElement.GetProperty("symbol").GetString().Should().Be("$");  // the source's
        shown.RootElement.TryGetProperty("displayBold", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetTable_References_CarryTheKeyTheyShow_AsTheRecordReadResolvesIt()
    {
        var table = MakeTable();
        // Department (88) has a Set Key (Name, Text); Project (99) none; Client (77) has a Set Key but the
        // relationship's Display Key (Since, Date) overrides it.
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField>
        {
            new() { Id = 1, Fid = 10, Name = "Department", TypeCode = "Reference", Settings = "{\"relationshipId\":5,\"parentTableId\":88}" },
            new() { Id = 2, Fid = 11, Name = "Project", TypeCode = "Reference", Settings = "{\"relationshipId\":6,\"parentTableId\":99}" },
            new() { Id = 3, Fid = 12, Name = "Client", TypeCode = "Reference", Settings = "{\"relationshipId\":7,\"parentTableId\":77}" },
            new() { Id = 4, Fid = 13, Name = "Notes", TypeCode = "Text" },
        });
        _relRepo.ListByChildTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 5, ParentTableId = 88, ChildTableId = table.Id, ReferenceFid = 10 },
            new() { Id = 6, ParentTableId = 99, ChildTableId = table.Id, ReferenceFid = 11 },
            new() { Id = 7, ParentTableId = 77, ChildTableId = table.Id, ReferenceFid = 12, DisplayKeyFieldId = 771 },
        });
        _tableRepo.GetByIdAsync(88, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 88, KeyFieldId = 881 });
        _tableRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 99 });
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, KeyFieldId = 772 });
        _fieldRepo.ListByTableAsync(88).Returns(new List<AppField> { new() { Id = 881, Fid = 6, Name = "Name", TypeCode = "Text" } });
        _fieldRepo.ListByTableAsync(99).Returns(new List<AppField>());
        _fieldRepo.ListByTableAsync(77).Returns(new List<AppField>
        {
            new() { Id = 771, Fid = 8, Name = "Since", TypeCode = "Date" },
            new() { Id = 772, Fid = 9, Name = "Code", TypeCode = "Text" },
        });
        var sut = new GetTableQueryHandler(_tableRepo, _fieldRepo, _relRepo);

        var result = await sut.HandleAsync(new GetTableQuery(table.PublicId));

        result.ReferenceKeys.Should().BeEquivalentTo(new Dictionary<long, ReferenceKey>
        {
            [1] = new(6, "Text"),       // the parent's Set Key
            [2] = new(3, "Number"),     // Record ID#
            [3] = new(8, "Date"),       // the relationship's Display Key, over the parent's Set Key
        });
    }

    [Fact]
    public async Task GetTable_NoReferences_ReadsNoRelationships()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { new() { Id = 1, Fid = 6, Name = "Name", TypeCode = "Text" } });
        var sut = new GetTableQueryHandler(_tableRepo, _fieldRepo, _relRepo);

        var result = await sut.HandleAsync(new GetTableQuery(table.PublicId));

        result.ReferenceKeys.Should().BeEmpty();
        await _relRepo.DidNotReceiveWithAnyArgs().ListByChildTableAsync(default, default);
    }

    [Fact]
    public async Task GetTable_NoMinMaxSummaries_ReadsNoChildTables()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField>
        {
            new() { Id = 2, Fid = 31, Name = "Tasks", TypeCode = "Summary", Settings = "{\"childTableId\":77,\"referenceFid\":10,\"function\":\"Count\"}" },
        });
        var sut = new GetTableQueryHandler(_tableRepo, _fieldRepo);

        await sut.HandleAsync(new GetTableQuery(table.PublicId));

        await _fieldRepo.DidNotReceive().ListByTableAsync(77, Arg.Any<CancellationToken>());
    }

    // --- ListTablesQueryHandler ---

    [Fact]
    public async Task ListTables_ReturnsTablesForApp()
    {
        var app = MakeApp();
        var items = new List<PowerBase.Application.Common.Models.AppTableListItemDto>
        {
            new() { PublicId = Guid.NewGuid(), Name = "Contacts" },
            new() { PublicId = Guid.NewGuid(), Name = "Companies" },
        };
        _appRepo.GetByPublicIdAsync(app.PublicId).Returns(app);
        _tableRepo.ListByAppPagedAsync(app.Id, 1, 20, null, "name", false, null, Arg.Any<CancellationToken>()).Returns(items);
        _tableRepo.CountByAppAsync(app.Id, null, null, Arg.Any<CancellationToken>()).Returns(2);
        var sut = new ListTablesQueryHandler(_appRepo, _tableRepo);

        var result = await sut.HandleAsync(new ListTablesQuery(app.PublicId));

        result.Items.Should().HaveCount(2);
        result.Total.Should().Be(2);
    }

    // --- ListTableNavItemsQueryHandler ---

    [Fact]
    public async Task ListTableNavItems_ReturnsEveryTableForApp_Unpaginated()
    {
        var app = MakeApp();
        var items = new List<PowerBase.Application.Common.Models.AppTableNavItemDto>
        {
            new() { PublicId = Guid.NewGuid(), Name = "Contacts", IsShowInBar = true },
            new() { PublicId = Guid.NewGuid(), Name = "Companies", IsShowInBar = false },
        };
        _appRepo.GetByPublicIdAsync(app.PublicId).Returns(app);
        _tableRepo.ListNavByAppAsync(app.Id, Arg.Any<CancellationToken>()).Returns(items);
        var sut = new ListTableNavItemsQueryHandler(_appRepo, _tableRepo);

        var result = await sut.HandleAsync(new ListTableNavItemsQuery(app.PublicId));

        result.Should().HaveCount(2);
        result.Should().Contain(t => t.Name == "Contacts" && t.IsShowInBar);
        result.Should().Contain(t => t.Name == "Companies" && !t.IsShowInBar);
    }
}
