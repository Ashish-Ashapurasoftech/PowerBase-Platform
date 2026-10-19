using System.Data;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// Covers ReferenceRelinkService: an Employee whose Department text matched no Department record (reference blank, text kept in
/// the hidden "(original)" field) is linked once a Department with that name is added, or an existing one is renamed to it.
/// The save only queues a job (never reading the Employee table); the background job links by each Department's current name.
/// </summary>
public class ReferenceRelinkServiceTests
{
    private const long ParentTableId = 10;
    private const long ChildTableId = 20;

    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _records = Substitute.For<IRecordRepository>();
    private readonly IAuditRepository _audit = Substitute.For<IAuditRepository>();
    private readonly IMessagePublisher _publisher = Substitute.For<IMessagePublisher>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IReferenceRelinkQueue _queue = Substitute.For<IReferenceRelinkQueue>();
    private readonly IDbTransaction _tx = Substitute.For<IDbTransaction>();

    private static readonly AppTable Department = new() { Id = ParentTableId, PublicId = Guid.NewGuid(), Name = "Department", AppId = 1 };
    private static readonly AppTable Employee = new() { Id = ChildTableId, PublicId = Guid.NewGuid(), Name = "Employee", AppId = 1 };

    private static readonly AppField RecordId = Field(3, 3, "Record ID#", "Number", isSystem: true);
    private static readonly AppField DeptName = Field(39, 6, "Department Name", "Text");
    private static readonly AppField DeptBudget = Field(41, 7, "Budget", "Number");
    private static readonly AppField Original = Field(51, 9, "Department (original)", "Text");

    private AppField _reference = Field(50, 8, "Department", "Reference", settings: """{"relationshipId":1,"parentTableId":10,"originalFid":9}""");
    private Relationship _rel = new() { Id = 1, ParentTableId = ParentTableId, ChildTableId = ChildTableId, ReferenceFieldId = 50, ReferenceFid = 8, DisplayKeyFieldId = 39 };
    private IReadOnlyDictionary<long, object?> _currentNames = new Dictionary<long, object?>();
    private IReadOnlyList<(long Id, string Text)> _unlinked = [];
    private IReadOnlyDictionary<long, long>? _linkedArg;

    private readonly ReferenceRelinkService _service;

    private static AppField Field(long id, int fid, string name, string typeCode, bool isSystem = false, string? settings = null) =>
        new() { Id = id, Fid = fid, Name = name, Label = name, TypeCode = typeCode, IsSystem = isSystem, Settings = settings, PublicId = Guid.NewGuid() };

    public ReferenceRelinkServiceTests()
    {
        _service = new ReferenceRelinkService(_relRepo, _tableRepo, _fieldRepo, _records, _audit, _publisher, _queryContext, _queue,
            NullLogger<ReferenceRelinkService>.Instance);

        _relRepo.ListByParentTableAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(_ => [_rel]);
        _fieldRepo.ListByTableAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns([RecordId, DeptName, DeptBudget]);
        _fieldRepo.ListByTableAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(_ => [_reference, Original]);
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(Department);
        _tableRepo.GetByIdAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(Employee);

        _records.GetColumnValuesByIdsAsync(Arg.Is(Department), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _currentNames);
        _records.ListUnlinkedOriginalsAsync(Arg.Is(Employee), Arg.Any<AppField>(), Arg.Is(Original), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(_ => _unlinked);
        _records.LinkUnlinkedReferencesAsync(Arg.Is(Employee), Arg.Any<AppField>(), Arg.Is(Original), Arg.Any<IReadOnlyDictionary<long, long>>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _linkedArg = call.ArgAt<IReadOnlyDictionary<long, long>>(3);
                return (IReadOnlyList<(Guid, long)>)_linkedArg.Select(p => (Guid.NewGuid(), p.Value)).ToList();
            });
    }

    private static RelinkParent Dept(long id, object? name) => new(id, new Dictionary<long, object?> { [6] = name });

    private Task AssertNothingQueued() =>
        _queue.DidNotReceiveWithAnyArgs().EnqueueAfterCommitAsync(default, default!, default);

    // ── The save: decides whether to queue, never reads the child table ─────────────────────────────

    [Fact]
    public async Task QueueAddedAsync_NewDepartmentWithName_QueuesItWithoutReadingEmployees()
    {
        await _service.QueueAddedAsync(Department, [Dept(500, "Marketing")]);

        await _queue.Received(1).EnqueueAfterCommitAsync(ParentTableId,
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 500 })), Arg.Any<CancellationToken>());
        await _records.DidNotReceiveWithAnyArgs().ListUnlinkedOriginalsAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task QueueAddedAsync_NameBlank_QueuesNothing()
    {
        await _service.QueueAddedAsync(Department, [Dept(500, "  ")]);

        await AssertNothingQueued();
    }

    [Fact]
    public async Task QueueAddedAsync_ReferenceKeepsNoOriginalText_QueuesNothing()
    {
        _reference = Field(50, 8, "Department", "Reference", settings: """{"relationshipId":1,"parentTableId":10}""");

        await _service.QueueAddedAsync(Department, [Dept(500, "Marketing")]);

        await AssertNothingQueued();
    }

    [Fact]
    public async Task QueueAddedAsync_KeyIsRecordId_QueuesNothing()
    {
        _rel = new() { Id = 1, ParentTableId = ParentTableId, ChildTableId = ChildTableId, ReferenceFieldId = 50, ReferenceFid = 8, DisplayKeyFieldId = null };

        await _service.QueueAddedAsync(Department, [new RelinkParent(500, new Dictionary<long, object?> { [3] = 500L })]);

        await AssertNothingQueued();
    }

    [Fact]
    public async Task QueueAddedAsync_QueueFails_DoesNotFailTheSave()
    {
        _queue.EnqueueAfterCommitAsync(Arg.Any<long>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("control DB down"));

        var act = () => _service.QueueAddedAsync(Department, [Dept(500, "Marketing")]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task QueueIfKeyChangedAsync_KeyUnchanged_QueuesNothing()
    {
        var before = new RelinkSnapshot(500, new Dictionary<long, object?> { [6] = "Marketing" });

        await _service.QueueIfKeyChangedAsync(Department, [before], new Dictionary<long, object?> { [6] = " marketing", [7] = 5m });

        await AssertNothingQueued();
    }

    [Fact]
    public async Task QueueIfKeyChangedAsync_Renamed_QueuesTheRecord()
    {
        var before = new RelinkSnapshot(500, new Dictionary<long, object?> { [6] = "Mktg" });

        await _service.QueueIfKeyChangedAsync(Department, [before], new Dictionary<long, object?> { [6] = "Marketing" });

        await _queue.Received(1).EnqueueAfterCommitAsync(ParentTableId,
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 500 })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueIfKeyChangedAsync_SameNameGivenToSeveralRecords_QueuesNothing()
    {
        RelinkSnapshot[] before =
        [
            new(500, new Dictionary<long, object?> { [6] = "Mktg" }),
            new(501, new Dictionary<long, object?> { [6] = "Sales" }),
        ];

        await _service.QueueIfKeyChangedAsync(Department, before, new Dictionary<long, object?> { [6] = "Marketing" });

        await AssertNothingQueued();
    }

    [Fact]
    public async Task QueueIfKeyChangedAsync_NameCleared_QueuesNothing()
    {
        var before = new RelinkSnapshot(500, new Dictionary<long, object?> { [6] = "Mktg" });

        await _service.QueueIfKeyChangedAsync(Department, [before], new Dictionary<long, object?> { [6] = null });

        await AssertNothingQueued();
    }

    [Fact]
    public async Task SnapshotAsync_WriteLeavesKeyAlone_ReturnsNullWithoutReading()
    {
        var snapshot = await _service.SnapshotAsync(Department, [RecordId, DeptName, DeptBudget], Guid.NewGuid(), [7L], _tx);

        snapshot.Should().BeNull();
        await _records.DidNotReceiveWithAnyArgs().GetByPublicIdAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task SnapshotAsync_WriteSetsKey_ReturnsTheKeyBefore()
    {
        var publicId = Guid.NewGuid();
        _records.GetByPublicIdAsync(Department, Arg.Any<IReadOnlyList<AppField>>(), publicId, _tx, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["Id"] = 500L, ["f_6"] = "Mktg", ["f_7"] = 10m });

        var snapshot = await _service.SnapshotAsync(Department, [RecordId, DeptName, DeptBudget], publicId, [6L, 7L], _tx);

        snapshot.Should().NotBeNull();
        snapshot!.RecordId.Should().Be(500);
        snapshot.Values.Should().BeEquivalentTo(new Dictionary<long, object?> { [6] = "Mktg" });
    }

    // ── The background job: links by each Department's current name ─────────────────────────────────

    [Fact]
    public async Task RunAsync_KeptTextMatchesTheName_LinksThoseEmployees()
    {
        _currentNames = new Dictionary<long, object?> { [500] = "Marketing" };
        _unlinked = [(100, "Marketing"), (101, "  marketing "), (102, "Sales")];

        var linked = await _service.RunAsync(ParentTableId, [500]);

        linked.Should().Be(2);
        _linkedArg.Should().BeEquivalentTo(new Dictionary<long, long> { [100] = 500, [101] = 500 });
        await _audit.Received(1).LogActivitiesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 2),
            Arg.Any<string?>(), 1, Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NameChangedAfterQueueing_UsesTheCurrentName()
    {
        _currentNames = new Dictionary<long, object?> { [500] = "Sales" };
        _unlinked = [(100, "Marketing"), (102, "Sales")];

        await _service.RunAsync(ParentTableId, [500]);

        _linkedArg.Should().BeEquivalentTo(new Dictionary<long, long> { [102] = 500 });
    }

    [Fact]
    public async Task RunAsync_RecordDeletedAfterQueueing_ReadsNoEmployees()
    {
        _currentNames = new Dictionary<long, object?>();

        var linked = await _service.RunAsync(ParentTableId, [500]);

        linked.Should().Be(0);
        await _records.DidNotReceiveWithAnyArgs().ListUnlinkedOriginalsAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task RunAsync_TableDeletedAfterQueueing_LinksNothing()
    {
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>()).ThrowsAsync(new NotFoundException("Table", ParentTableId));

        var linked = await _service.RunAsync(ParentTableId, [500]);

        linked.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_SameNameOnTwoRecords_LinksNeither()
    {
        _currentNames = new Dictionary<long, object?> { [500] = "Marketing", [501] = "MARKETING", [502] = "Sales" };
        _unlinked = [(100, "Marketing"), (101, "Sales")];

        await _service.RunAsync(ParentTableId, [500, 501, 502]);

        _linkedArg.Should().BeEquivalentTo(new Dictionary<long, long> { [101] = 502 });
    }

    [Fact]
    public async Task RunAsync_NumberKey_MatchesByValue()
    {
        _rel = new() { Id = 1, ParentTableId = ParentTableId, ChildTableId = ChildTableId, ReferenceFieldId = 50, ReferenceFid = 8, DisplayKeyFieldId = 41 };
        _currentNames = new Dictionary<long, object?> { [500] = 1000.0000m };
        _unlinked = [(100, "1000"), (101, "999")];

        await _service.RunAsync(ParentTableId, [500]);

        _linkedArg.Should().BeEquivalentTo(new Dictionary<long, long> { [100] = 500 });
    }

    [Fact]
    public async Task RunAsync_ReadFails_ThrowsSoTheJobIsRetried()
    {
        _currentNames = new Dictionary<long, object?> { [500] = "Marketing" };
        _records.ListUnlinkedOriginalsAsync(Arg.Is(Employee), Arg.Any<AppField>(), Arg.Is(Original), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("timeout"));

        var act = () => _service.RunAsync(ParentTableId, [500]);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RunAsync_SelfRelationship_NeverLinksARecordToItself()
    {
        var self = new AppTable { Id = ParentTableId, PublicId = Guid.NewGuid(), Name = "Employee", AppId = 1 };
        var manager = Field(52, 10, "Manager", "Reference", settings: """{"relationshipId":2,"parentTableId":10,"originalFid":11}""");
        var managerOriginal = Field(53, 11, "Manager (original)", "Text");
        var name = Field(54, 12, "Name", "Text");
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(self);
        _relRepo.ListByParentTableAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(
            [new Relationship { Id = 2, ParentTableId = ParentTableId, ChildTableId = ParentTableId, ReferenceFieldId = 52, ReferenceFid = 10, DisplayKeyFieldId = 54 }]);
        _fieldRepo.ListByTableAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns([RecordId, name, manager, managerOriginal]);
        _records.GetColumnValuesByIdsAsync(Arg.Is(self), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, object?> { [500] = "Asha" });
        _records.ListUnlinkedOriginalsAsync(Arg.Is(self), Arg.Is(manager), Arg.Is(managerOriginal), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns([(500L, "Asha"), (501L, "Asha")]);
        IReadOnlyDictionary<long, long>? linkedArg = null;
        _records.LinkUnlinkedReferencesAsync(Arg.Is(self), Arg.Is(manager), Arg.Is(managerOriginal),
                Arg.Do<IReadOnlyDictionary<long, long>>(a => linkedArg = a), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns([(Guid.NewGuid(), 500L)]);

        await _service.RunAsync(ParentTableId, [500]);

        linkedArg.Should().BeEquivalentTo(new Dictionary<long, long> { [501] = 500 });
    }

    [Fact]
    public async Task RunAsync_SearchableReference_PublishesIndexUpdate()
    {
        _reference = Field(50, 8, "Department", "Reference", settings: """{"relationshipId":1,"parentTableId":10,"originalFid":9}""");
        _reference.IsSearchable = true;
        _currentNames = new Dictionary<long, object?> { [500] = "Marketing" };
        _unlinked = [(100, "Marketing")];

        await _service.RunAsync(ParentTableId, [500]);

        await _publisher.Received(1).PublishBatchAsync(
            Arg.Is<IEnumerable<SearchIndexMessage>>(m => m.Single().Payload!["8"]!.Equals(500L)), Arg.Any<CancellationToken>());
    }
}
