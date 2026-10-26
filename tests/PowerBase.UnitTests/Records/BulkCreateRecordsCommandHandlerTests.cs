using System.Data;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Records.Commands.BulkCreateRecords;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Records;

public class BulkCreateRecordsCommandHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IPipelineTriggerInterceptor _triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IMessagePublisher _messagePublisher = Substitute.For<IMessagePublisher>();
    private readonly FormulaEngine _engine = new();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IFormRuleRepository _formRuleRepo = Substitute.For<IFormRuleRepository>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();

    private readonly AppTable _table = new() { Id = 5, PublicId = Guid.NewGuid(), Name = "Invoice Lines", PhysicalTableName = PhysicalNaming.TableName(5) };
    private readonly AppField _recordId = new() { Id = 1, Fid = 3, Name = "S_recordId", Label = "Record ID#", IsSystem = true, PhysicalColumnName = "Id" };
    private readonly AppField _product = new() { Id = 10, Fid = 6, Name = "C_product", Label = "Product", TypeCode = "Text" };
    private readonly AppField _qty = new() { Id = 11, Fid = 7, Name = "C_qty", Label = "Qty", TypeCode = "Number" };
    private readonly AppField _lineNo = new() { Id = 12, Fid = 8, Name = "C_lineNo", Label = "Line No", TypeCode = "Text", IsUnique = true };
    private readonly AppField _total = new() { Id = 13, Fid = 9, Name = "C_total", Label = "Total", TypeCode = "Formula" };

    /// <summary>Row Ids handed out by the fake insert: the n-th created record gets Record ID# 100 + n.</summary>
    private readonly List<Guid> _created = [];

    public BulkCreateRecordsCommandHandlerTests()
    {
        _appRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new App());
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = true });
        _tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        UseFields(_recordId, _product, _qty, _lineNo, _total);

        _recordRepo.CreateManyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
                Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>?>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyList<IReadOnlyDictionary<long, object?>>>().Select(_ => Guid.NewGuid()).ToList();
                _created.AddRange(ids);
                return (IReadOnlyList<Guid>)ids;
            });
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyDictionary<Guid, long>)ci.Arg<IReadOnlyCollection<Guid>>()
                .ToDictionary(id => id, id => 100L + _created.IndexOf(id)));
    }

    private void UseFields(params AppField[] fields) =>
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(fields.ToList());

    private BulkCreateRecordsCommandHandler CreateSut() => new(
        _tableRepo, _fieldRepo, _recordRepo, _relRepo, _enforcer, _auditRepo, _triggerInterceptor, _uow, _queryContext, _userRepo,
        _messagePublisher, _engine, _appRepo, _formRuleRepo, _formRepo, _appUserRepo, _reportRepo);

    private BulkCreateRecordsCommand Command(params Dictionary<long, object?>[] rows) =>
        new(_table.PublicId, rows.Select(r => (IReadOnlyDictionary<long, object?>)r).ToList());

    private Task WhenCreateManyCalled() => _recordRepo.Received(1).CreateManyAsync(
        Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
        Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>?>());

    private Task WhenCreateManyNotCalled() => _recordRepo.DidNotReceive().CreateManyAsync(
        Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(),
        Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>?>());

    [Fact]
    public async Task Handle_ValidRows_CreatesAllInOneTransactionAndReturnsThemInOrder()
    {
        var result = await CreateSut().HandleAsync(Command(
            new() { [6L] = "Widget", [7L] = 2m },
            new() { [6L] = "Bolt", [7L] = 10m },
            new() { [6L] = "Nut", [7L] = 4m }));

        result.Should().HaveCount(3);
        result.Select(r => r.Fields["6"]).Should().Equal("Widget", "Bolt", "Nut");
        result.Select(r => r.Fields["3"]).Should().Equal(100L, 101L, 102L);
        result.Select(r => r.Id).Should().Equal(_created);
        await WhenCreateManyCalled();
        await _uow.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await _tableRepo.Received(1).IncrementRecordCountByAsync(_table.Id, 3, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
        await _auditRepo.Received(1).LogActivitiesAsync(AuditActions.Created, AuditEntityTypes.Record,
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 3), Arg.Any<string?>(), _table.AppId, Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RowsFillingDifferentCells_InsertsEveryRowWithTheSameColumns()
    {
        // Same INSERT shape for every row keeps their Record IDs in the order the user entered them.
        await CreateSut().HandleAsync(Command(
            new() { [6L] = "Widget", [7L] = 2m },
            new() { [6L] = "Bolt" }));

        await _recordRepo.Received(1).CreateManyAsync(_table, Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(rows =>
                rows.Count == 2 && rows[1].ContainsKey(7L) && rows[1][7L] == null && rows[0].Keys.OrderBy(k => k).SequenceEqual(rows[1].Keys.OrderBy(k => k))),
            Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>?>());
    }

    [Fact]
    public async Task Handle_OneRowMissingRequiredValue_ReportsThatRowAndWritesNothing()
    {
        var qty = new AppField { Id = 11, Fid = 7, Name = "C_qty", Label = "Qty", TypeCode = "Number", IsRequired = true };
        UseFields(_product, qty);

        var act = () => CreateSut().HandleAsync(Command(
            new() { [6L] = "Widget", [7L] = 2m },
            new() { [6L] = "Bolt" },
            new() { [6L] = "Nut", [7L] = 4m }));

        var error = await act.Should().ThrowAsync<BulkCreateValidationException>();
        error.Which.Violations.Should().ContainSingle()
            .Which.Should().Match<BulkCreateRowViolation>(v => v.RowIndex == 1 && v.FieldId == 7 && v.ConstraintType == "Required");
        error.Which.Message.Should().StartWith("Row 2:");
        await WhenCreateManyNotCalled();
        await _uow.DidNotReceive().BeginAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TwoRowsWithSameUniqueValue_FlagsBothRowsAndWritesNothing()
    {
        var act = () => CreateSut().HandleAsync(Command(
            new() { [6L] = "Widget", [8L] = "L-1" },
            new() { [6L] = "Bolt", [8L] = "L-2" },
            new() { [6L] = "Nut", [8L] = "l-1 " }));   // same value: case and trailing spaces don't count

        var error = await act.Should().ThrowAsync<BulkCreateValidationException>();
        error.Which.Violations.Should().HaveCount(2);
        error.Which.Violations.Select(v => v.RowIndex).Should().Equal(0, 2);
        error.Which.Violations.Should().OnlyContain(v => v.FieldId == 8 && v.ConstraintType == "Unique");
        error.Which.Violations[0].Message.Should().Contain("row 3 of this save");
        await WhenCreateManyNotCalled();
    }

    [Fact]
    public async Task Handle_UniqueValueAlreadyInTable_ReportsRowViolation()
    {
        _recordRepo.HasValueDuplicateAsync(_table, _lineNo, "L-9", Arg.Any<long?>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>()).Returns(true);

        var act = () => CreateSut().HandleAsync(Command(
            new() { [6L] = "Widget", [8L] = "L-1" },
            new() { [6L] = "Bolt", [8L] = "L-9" }));

        var error = await act.Should().ThrowAsync<BulkCreateValidationException>();
        error.Which.Violations.Should().ContainSingle()
            .Which.Should().Match<BulkCreateRowViolation>(v => v.RowIndex == 1 && v.FieldId == 8 && v.ConstraintType == "Unique");
    }

    [Fact]
    public async Task Handle_ReferenceToMissingParent_ReportsThatRowsField()
    {
        var parent = new AppTable { Id = 9, PublicId = Guid.NewGuid(), Name = "Products", PhysicalTableName = PhysicalNaming.TableName(9) };
        var productRef = new AppField { Id = 14, Fid = 10, Name = "C_productRef", Label = "Product", TypeCode = "Reference", Settings = "{\"parentTableId\":9}" };
        UseFields(_product, productRef);
        _tableRepo.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(parent);
        _recordRepo.ExistsAsync(parent, 1, Arg.Any<CancellationToken>()).Returns(true);

        var act = () => CreateSut().HandleAsync(Command(
            new() { [6L] = "Widget", [10L] = "1" },
            new() { [6L] = "Bolt", [10L] = "no such product" }));

        var error = await act.Should().ThrowAsync<BulkCreateValidationException>();
        error.Which.Violations.Should().ContainSingle()
            .Which.Should().Match<BulkCreateRowViolation>(v => v.RowIndex == 1 && v.FieldId == 10);
        await WhenCreateManyNotCalled();
    }

    [Fact]
    public async Task Handle_FormulaFieldSubmitted_ThrowsValidationException()
    {
        var act = () => CreateSut().HandleAsync(Command(new Dictionary<long, object?> { [6L] = "Widget", [9L] = 20m }));

        await act.Should().ThrowAsync<ValidationException>();
        await WhenCreateManyNotCalled();
    }

    [Fact]
    public async Task Handle_UserCannotAddRecords_ThrowsUnauthorized()
    {
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = false, CanAdd = false });

        var act = () => CreateSut().HandleAsync(Command(new Dictionary<long, object?> { [6L] = "Widget" }));

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await WhenCreateManyNotCalled();
    }

    [Fact]
    public async Task Handle_NoRows_ThrowsValidationException()
    {
        var act = () => CreateSut().HandleAsync(Command());

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Handle_MoreRowsThanTheLimit_ThrowsValidationException()
    {
        var rows = Enumerable.Range(0, RecordLimits.MaxBulkCreate + 1).Select(i => new Dictionary<long, object?> { [6L] = $"P{i}" }).ToArray();

        var act = () => CreateSut().HandleAsync(Command(rows));

        await act.Should().ThrowAsync<ValidationException>();
        await WhenCreateManyNotCalled();
    }

    [Fact]
    public async Task Handle_GridEditSave_ChecksOnlyTheReportsGridEditRules()
    {
        var reportId = Guid.NewGuid();
        _reportRepo.GetAppTableIdByPublicIdAsync(reportId, Arg.Any<CancellationToken>()).Returns(_table.Id);

        await CreateSut().HandleAsync(new BulkCreateRecordsCommand(_table.PublicId,
            [new Dictionary<long, object?> { [6L] = "Widget" }], reportId, IsGridEditSave: true));

        await _reportRepo.Received(1).GetAppliedGridEditRulesAsync(reportId, Arg.Any<CancellationToken>());
        await _formRuleRepo.DidNotReceive().ListActiveByTableIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotAGridEditSave_ChecksEveryActiveFormRule()
    {
        await CreateSut().HandleAsync(Command(new Dictionary<long, object?> { [6L] = "Widget" }));

        await _formRuleRepo.Received(1).ListActiveByTableIdAsync(_table.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TableHasPipelineListeners_RaisesRecordAddedForEveryRow()
    {
        _triggerInterceptor.HasActiveSubscriptionsAsync(_table, Arg.Any<CancellationToken>()).Returns(true);

        await CreateSut().HandleAsync(Command(new() { [6L] = "Widget" }, new() { [6L] = "Bolt" }));

        await _triggerInterceptor.Received(1).InterceptAddedManyAsync(_table, Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<IReadOnlyList<(Guid RecordPublicId, IReadOnlyDictionary<long, object?> Values)>>(records =>
                records.Count == 2 && Equals(records[0].Values[3L], 100L) && Equals(records[1].Values[3L], 101L)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InsertFails_RollsBack()
    {
        _tableRepo.IncrementRecordCountByAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        var act = () => CreateSut().HandleAsync(Command(new Dictionary<long, object?> { [6L] = "Widget" }));

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _uow.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }
}
