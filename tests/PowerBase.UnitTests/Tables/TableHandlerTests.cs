using FluentAssertions;
using PowerBase.Application.Relationships.Commands.DeleteRelationship;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tables.Commands.CreateTable;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Application.Tables.Queries.GetTable;
using PowerBase.Application.Tables.Queries.ListTables;
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
        _appSeeder.CreateTableWithDefaultsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
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
        await _appSeeder.Received(1).CreateTableWithDefaultsAsync(Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
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
        var sut = new DeleteTableCommandHandler(_tableRepo, _auditRepo, _relRepo, deleteRelHandler);

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
    }

    // --- ListTablesQueryHandler ---

    [Fact]
    public async Task ListTables_ReturnsTablesForApp()
    {
        var app = MakeApp();
        var tables = new List<AppTable> { MakeTable(), MakeTable(21) };
        _appRepo.GetByPublicIdAsync(app.PublicId).Returns(app);
        _tableRepo.ListByAppAsync(app.Id).Returns(tables);
        var sut = new ListTablesQueryHandler(_appRepo, _tableRepo);

        var result = await sut.HandleAsync(new ListTablesQuery(app.PublicId));

        result.Should().HaveCount(2);
    }
}
