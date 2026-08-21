using System.Data;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
using PowerBase.Application.Records.Commands.CreateRecord;
using PowerBase.Application.Records.Commands.DeleteRecord;
using PowerBase.Application.Records.Commands.UpdateRecord;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Application.Records.Queries.ListRecords;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Records;

public class RecordHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();
    private readonly IFormulaDefaultResolver _formulaDefaults = Substitute.For<IFormulaDefaultResolver>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly PowerBase.Application.Relationships.IRelationalProjector _relationalProjector = Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>();
    private readonly IPipelineTriggerInterceptor _triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();

    public RecordHandlerTests()
    {
        // Default to unrestricted access; the visible-field set echoes the input.
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>() ?? new List<AppField>(),
            });

        // Default: no formula fields — an empty computed-value map per row.
        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.Arg<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
                .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());

        // Default: no relationship fields — an empty computed-value map per row.
        _relationalProjector.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<IReadOnlyDictionary<long, object?>>)ci.Arg<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
                .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());

        // Default: no relationships where this table is the parent (delete restrict is a no-op).
        _relRepo.ListByParentTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<Relationship>());
    }

    private static AppTable MakeTable(long id = 5) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = "T",
        PhysicalTableName = PhysicalNaming.TableName(id),
    };

    private static AppField MakeField(long id = 1) => new() { Id = id, Fid = (int)id, Name = "Field" };

    private static IReadOnlyDictionary<string, object?> MakeRow(Guid publicId) =>
        new Dictionary<string, object?>
        {
            ["PublicId"] = publicId,
            ["CreatedOn"] = DateTime.UtcNow,
        };

    // --- CreateRecordCommandHandler ---

    [Fact]
    public async Task CreateRecord_ValidCommand_ReturnsRecordResult()
    {
        var table = MakeTable();
        var field = MakeField(1);
        var publicId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field });
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(publicId);
        var sut = new CreateRecordCommandHandler(_tableRepo, _fieldRepo, _recordRepo, _enforcer, _auditRepo, _formulaDefaults, _triggerInterceptor, _uow, Substitute.For<IMessagePublisher>());

        var result = await sut.HandleAsync(new CreateRecordCommand(table.PublicId,
            new Dictionary<long, object?> { [1L] = "Alice" }));

        result.Id.Should().Be(publicId);
        result.Fields["1"].Should().Be("Alice");
    }

    [Fact]
    public async Task CreateRecord_UnknownFieldId_ThrowsValidationException()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { MakeField(1) });
        var sut = new CreateRecordCommandHandler(_tableRepo, _fieldRepo, _recordRepo, _enforcer, _auditRepo, _formulaDefaults, _triggerInterceptor, _uow, Substitute.For<IMessagePublisher>());

        await sut.Invoking(s => s.HandleAsync(new CreateRecordCommand(table.PublicId,
                new Dictionary<long, object?> { [999L] = "X" })))
            .Should().ThrowAsync<ValidationException>();
    }

    // --- UpdateRecordCommandHandler ---

    [Fact]
    public async Task UpdateRecord_ValidFieldValues_CallsUpdateOnRepo()
    {
        var table = MakeTable();
        var field = MakeField(1);
        var recordId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field });
        IRecordWriteService writeService = new RecordWriteService(_tableRepo, _fieldRepo, _recordRepo, _appUserRepo, _auditRepo, _triggerInterceptor);
        var sut = new UpdateRecordCommandHandler(_tableRepo, _fieldRepo, _enforcer, writeService, _uow, Substitute.For<IMessagePublisher>());

        await sut.HandleAsync(new UpdateRecordCommand(table.PublicId, recordId,
            new Dictionary<long, object?> { [1L] = "Updated" }));

        await _recordRepo.Received(1).UpdateAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), recordId,
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateRecord_EmptyFieldValues_SkipsUpdate()
    {
        var table = MakeTable();
        IRecordWriteService writeService = new RecordWriteService(_tableRepo, _fieldRepo, _recordRepo, _appUserRepo, _auditRepo, _triggerInterceptor);
        var sut = new UpdateRecordCommandHandler(_tableRepo, _fieldRepo, _enforcer, writeService, _uow, Substitute.For<IMessagePublisher>());

        await sut.HandleAsync(new UpdateRecordCommand(table.PublicId, Guid.NewGuid(),
            new Dictionary<long, object?>()));

        await _recordRepo.DidNotReceive().UpdateAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    // --- DeleteRecordCommandHandler ---

    [Fact]
    public async Task DeleteRecord_CallsDeleteOnRepo()
    {
        var table = MakeTable();
        var recordId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        var sut = new DeleteRecordCommandHandler(_tableRepo, _fieldRepo, _recordRepo, _enforcer, _auditRepo, _relRepo, _triggerInterceptor, _uow, Substitute.For<IMessagePublisher>());

        await sut.HandleAsync(new DeleteRecordCommand(table.PublicId, recordId));

        await _recordRepo.Received(1).DeleteAsync(table, recordId, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    // --- GetRecordQueryHandler ---

    [Fact]
    public async Task GetRecord_ReturnsRecordResult()
    {
        var table = MakeTable();
        var field = MakeField(1);
        var recordPublicId = Guid.NewGuid();
        var row = MakeRow(recordPublicId);
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field });
        _recordRepo.GetByPublicIdAsync(table, Arg.Any<IReadOnlyList<AppField>>(), recordPublicId)
            .Returns(row);
        var sut = new GetRecordQueryHandler(_tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector);

        var result = await sut.HandleAsync(new GetRecordQuery(table.PublicId, recordPublicId));

        result.Id.Should().Be(recordPublicId);
    }

    // --- ListRecordsQueryHandler ---

    [Fact]
    public async Task ListRecords_ReturnsPagedResult()
    {
        var table = MakeTable();
        var field = MakeField(1);
        var row = MakeRow(Guid.NewGuid());
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field });
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, 20)
            .Returns(new List<IReadOnlyDictionary<string, object?>> { row });
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(1);
        var sut = new ListRecordsQueryHandler(_tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector);

        var result = await sut.HandleAsync(new ListRecordsQuery(table.PublicId, 1, 20));

        result.Items.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public async Task ListRecords_PageBelowOne_NormalizesToOne(int inputPage, int expectedPage)
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(Arg.Any<long>()).Returns(new List<AppField>());
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(0);
        var sut = new ListRecordsQueryHandler(_tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector);

        var result = await sut.HandleAsync(new ListRecordsQuery(table.PublicId, inputPage, 20));

        result.Page.Should().Be(expectedPage);
    }

    // ── Builder Data Isolation Tests ──────────────────────────────────────────
    // These tests verify that a user who has builder-level permissions (e.g. Schema Builder
    // has tables:create/update) but ViewScope = None on a table is FULLY blocked from
    // accessing any record data. Builder permissions and data access are always separate.

    [Fact]
    public async Task GetRecord_SchemaBuilderWithViewScopeNone_ThrowsNotFoundException()
    {
        // Arrange: Schema Builder role — can create/update tables but ViewScope = None.
        // The enforcer resolves ViewScope = None → CanView = false.
        var table = MakeTable();
        var recordId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { MakeField(1) });

        // Enforcer returns no record access for this table
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext
            {
                Unrestricted = false,
                ViewScope = RecordScopes.None,
                ModifyScope = RecordScopes.None,
                VisibleFields = [],
                EditableFieldIds = new HashSet<long>(),
            });

        var sut = new GetRecordQueryHandler(_tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector);

        // Act & Assert: Record must not be accessible — returned as NotFoundException
        // so the client cannot infer whether the record exists.
        await sut.Invoking(s => s.HandleAsync(new GetRecordQuery(table.PublicId, recordId)))
            .Should().ThrowAsync<NotFoundException>();

        // Repository must never be called — no DB hit for a blocked user
        await _recordRepo.DidNotReceive().GetByPublicIdAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRecords_SchemaBuilderWithViewScopeNone_ReturnsEmptyResult()
    {
        // Arrange: Schema Builder role — can create/update tables but ViewScope = None.
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { MakeField(1) });

        // Enforcer returns no record access for this table
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext
            {
                Unrestricted = false,
                ViewScope = RecordScopes.None,
                ModifyScope = RecordScopes.None,
                VisibleFields = [],
                EditableFieldIds = new HashSet<long>(),
            });

        var sut = new ListRecordsQueryHandler(_tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector);

        var result = await sut.HandleAsync(new ListRecordsQuery(table.PublicId, 1, 20));

        // Must return empty — no items, no count, no data leakage
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);

        // Repository must never be queried — short-circuits before any DB call
        await _recordRepo.DidNotReceive().ListAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }
}
