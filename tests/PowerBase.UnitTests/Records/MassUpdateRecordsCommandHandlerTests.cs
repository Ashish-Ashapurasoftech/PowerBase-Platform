using System.Data;
using PowerBase.Application.Common.Models;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Commands.MassUpdateRecords;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Records;

public class MassUpdateRecordsCommandHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private readonly IPipelineTriggerInterceptor _triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private readonly IMessagePublisher _messagePublisher = Substitute.For<IMessagePublisher>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private static AppTable MakeTable(long id = 5) => new() { Id = id, PublicId = Guid.NewGuid(), Name = "T" };

    private static AppField MakeField(int fid, bool isRequired = false, bool isUnique = false) =>
        new() { Id = fid, Fid = fid, Name = $"C_field{fid}", Label = $"Field {fid}", TypeCode = "Text", IsRequired = isRequired, IsUnique = isUnique };

    private MassUpdateRecordsCommandHandler CreateSut() => new(_tableRepo, _fieldRepo, _recordRepo, _enforcer, _auditRepo, _triggerInterceptor, _uow, _queryContext, _messagePublisher);

    public MassUpdateRecordsCommandHandlerTests()
    {
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = true });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MassUpdate_EnlistsWriteAndOnlyPublishesAfterCommit(bool failCommit)
    {
        var table = MakeTable(); var field = MakeField(1); var recordId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new[] { field });
        _recordRepo.GetIdsByPublicIdsMapAsync(table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long> { [recordId] = 1 });
        _recordRepo.GetByPublicIdAsync(table, Arg.Any<IReadOnlyList<AppField>>(), recordId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["f_1"] = "before" });
        var transaction = Substitute.For<IDbTransaction>();
        IDbTransaction? active = null;
        var events = new List<string>();
        _uow.Transaction.Returns(_ => active);
        _uow.BeginAsync(Arg.Any<CancellationToken>()).Returns(_ => { active = transaction; events.Add("begin"); return Task.CompletedTask; });
        _triggerInterceptor.InterceptBulkAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<PipelineRecordChange>>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(_ =>
            { Assert.Same(transaction, active); events.Add("outbox"); return Task.CompletedTask; });
        _recordRepo.MassUpdateAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>>(), Arg.Any<IDbTransaction>())
            .Returns(call =>
            {
                Assert.Same(transaction, call.ArgAt<IDbTransaction>(6));
                events.Add("write"); call.ArgAt<Action<SearchIndexMessage>>(5)(new SearchIndexMessage()); return Task.FromResult(1);
            });
        _uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            events.Add("commit");
            return failCommit ? Task.FromException(new OperationCanceledException()) : Task.CompletedTask;
        });
        _uow.RollbackAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Assert.Equal(CancellationToken.None, call.Arg<CancellationToken>()); events.Add("rollback"); return Task.CompletedTask;
        });
        _messagePublisher.PublishBatchAsync(Arg.Any<IEnumerable<SearchIndexMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { events.Add("publish"); return Task.CompletedTask; });
        var command = new MassUpdateRecordsCommand(table.PublicId, new[] { recordId }, new Dictionary<long, object?> { [1] = "after" });
        if (failCommit) await Assert.ThrowsAsync<OperationCanceledException>(() => CreateSut().HandleAsync(command));
        else Assert.Equal(1, await CreateSut().HandleAsync(command));
        Assert.Equal(new[] { "begin", "outbox", "write", "commit", failCommit ? "rollback" : "publish" }, events);
    }

    [Fact]
    public async Task HandleAsync_ValidRequest_UpdatesAllRecordsInOneCall()
    {
        var table = MakeTable();
        var field = MakeField(1);
        var recordIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var idMap = recordIds.Select((id, i) => (id, rowId: (long)(100 + i))).ToDictionary(x => x.id, x => x.rowId);

        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field });
        _recordRepo.GetIdsByPublicIdsMapAsync(table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, long>)idMap);
        _recordRepo.MassUpdateAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>>(), Arg.Any<IDbTransaction>())
            .Returns(3);

        var sut = CreateSut();
        var result = await sut.HandleAsync(new MassUpdateRecordsCommand(table.PublicId, recordIds, new Dictionary<long, object?> { [1L] = "Bulk Value" }));

        result.Should().Be(3);
        await _recordRepo.Received(1).MassUpdateAsync(table, Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.Count == 3), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>>(), Arg.Any<IDbTransaction>());
    }

    [Fact]
    public async Task HandleAsync_RequiredFieldSetBlank_ThrowsWithoutWriting()
    {
        var table = MakeTable();
        var field = MakeField(1, isRequired: true);
        var recordIds = new[] { Guid.NewGuid() };
        var idMap = new Dictionary<Guid, long> { [recordIds[0]] = 100L };

        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field });
        _recordRepo.GetIdsByPublicIdsMapAsync(table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, long>)idMap);

        var sut = CreateSut();
        var ex = await sut.Invoking(s => s.HandleAsync(new MassUpdateRecordsCommand(table.PublicId, recordIds, new Dictionary<long, object?> { [1L] = "" })))
            .Should().ThrowAsync<RecordConstraintViolationException>();

        ex.Which.Violations.Should().ContainSingle(v => v.RecordId == recordIds[0] && v.FieldId == 1 && v.ConstraintType == "Required");
        await _recordRepo.DidNotReceive().MassUpdateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<CancellationToken>(), Arg.Any<Action<SearchIndexMessage>>(), Arg.Any<IDbTransaction>());
    }

    [Fact]
    public async Task HandleAsync_UniqueFieldAcrossMultipleRecords_FlagsInRequestDuplicateForEachRecord()
    {
        var table = MakeTable();
        var field = MakeField(1, isUnique: true);
        var recordIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var idMap = new Dictionary<Guid, long> { [recordIds[0]] = 100L, [recordIds[1]] = 101L };

        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field });
        _recordRepo.GetIdsByPublicIdsMapAsync(table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, long>)idMap);

        var sut = CreateSut();
        var ex = await sut.Invoking(s => s.HandleAsync(new MassUpdateRecordsCommand(table.PublicId, recordIds, new Dictionary<long, object?> { [1L] = "same-value" })))
            .Should().ThrowAsync<RecordConstraintViolationException>();

        ex.Which.Violations.Should().HaveCount(2);
        ex.Which.Violations.Should().OnlyContain(v => v.ConstraintType == "Unique" && v.FieldId == 1);
    }

    [Fact]
    public async Task HandleAsync_UniqueFieldSingleRecordCollidesWithExisting_Throws()
    {
        var table = MakeTable();
        var field = MakeField(1, isUnique: true);
        var recordIds = new[] { Guid.NewGuid() };
        var idMap = new Dictionary<Guid, long> { [recordIds[0]] = 100L };

        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field });
        _recordRepo.GetIdsByPublicIdsMapAsync(table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, long>)idMap);
        _recordRepo.HasValueDuplicateAsync(table, field, "taken", 100L, Arg.Any<CancellationToken>()).Returns(true);

        var sut = CreateSut();
        var ex = await sut.Invoking(s => s.HandleAsync(new MassUpdateRecordsCommand(table.PublicId, recordIds, new Dictionary<long, object?> { [1L] = "taken" })))
            .Should().ThrowAsync<RecordConstraintViolationException>();

        ex.Which.Violations.Should().ContainSingle(v => v.RecordId == recordIds[0] && v.FieldId == 1 && v.ConstraintType == "Unique");
    }

    [Fact]
    public async Task HandleAsync_UnknownRecordId_FlagsNotFoundViolation()
    {
        var table = MakeTable();
        var field = MakeField(1);
        var knownId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        var idMap = new Dictionary<Guid, long> { [knownId] = 100L };

        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { field });
        _recordRepo.GetIdsByPublicIdsMapAsync(table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, long>)idMap);

        var sut = CreateSut();
        var ex = await sut.Invoking(s => s.HandleAsync(new MassUpdateRecordsCommand(table.PublicId, [knownId, unknownId], new Dictionary<long, object?> { [1L] = "X" })))
            .Should().ThrowAsync<RecordConstraintViolationException>();

        ex.Which.Violations.Should().ContainSingle(v => v.RecordId == unknownId && v.ConstraintType == "NotFound");
    }

    [Fact]
    public async Task HandleAsync_UnknownFieldId_ThrowsValidationException()
    {
        var table = MakeTable();
        _tableRepo.GetByPublicIdAsync(table.PublicId).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id).Returns(new List<AppField> { MakeField(1) });

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(new MassUpdateRecordsCommand(table.PublicId, [Guid.NewGuid()], new Dictionary<long, object?> { [999L] = "X" })))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_TooManyRecordIds_ThrowsValidationException()
    {
        var table = MakeTable();
        var sut = CreateSut();
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList();

        await sut.Invoking(s => s.HandleAsync(new MassUpdateRecordsCommand(table.PublicId, ids, new Dictionary<long, object?> { [1L] = "X" })))
            .Should().ThrowAsync<ValidationException>();
    }
}
