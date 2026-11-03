using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// A reference proxy opens its parent record through GetParentRecordQueryHandler: Employee (child) → Department
/// (parent). The link must never point at, or reveal, a record the user can't open.
/// </summary>
public class GetParentRecordQueryHandlerTests
{
    private const long ParentTableId = 10;
    private const long ChildTableId = 20;
    private const int ReferenceFid = 8;
    private const long ParentRowId = 12;
    private static readonly Guid RelPublicId = Guid.NewGuid();
    private static readonly Guid ChildRecordId = Guid.NewGuid();
    private static readonly Guid ParentRecordId = Guid.NewGuid();

    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IFormulaProjector _formulaProjector = Substitute.For<IFormulaProjector>();
    private readonly IRelationalProjector _relationalProjector = Substitute.For<IRelationalProjector>();

    private static readonly AppTable Parent = new() { Id = ParentTableId, PublicId = Guid.NewGuid(), Name = "Department" };
    private static readonly AppTable Child = new() { Id = ChildTableId, PublicId = Guid.NewGuid(), Name = "Employee" };
    private static readonly AppField Reference = new()
    {
        Id = 41, Fid = ReferenceFid, Name = "Related Department", TypeCode = "Reference", AppTableId = ChildTableId,
    };
    private static readonly AppField DeptName = new() { Id = 30, Fid = 6, Name = "Name", TypeCode = "Text", AppTableId = ParentTableId };

    private readonly GetParentRecordQueryHandler _handler;

    public GetParentRecordQueryHandlerTests()
    {
        _relRepo.GetByPublicIdAsync(RelPublicId, Arg.Any<CancellationToken>()).Returns(new Relationship
        {
            Id = 1, PublicId = RelPublicId, ParentTableId = ParentTableId, ChildTableId = ChildTableId,
            ReferenceFieldId = Reference.Id, ReferenceFid = ReferenceFid,
        });
        _tableRepo.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns(Parent);
        _tableRepo.GetByIdAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns(Child);
        _tableRepo.GetByPublicIdAsync(Parent.PublicId, Arg.Any<CancellationToken>()).Returns(Parent);
        _tableRepo.GetByPublicIdAsync(Child.PublicId, Arg.Any<CancellationToken>()).Returns(Child);
        _fieldRepo.ListByTableAsync(ChildTableId, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)[Reference]);
        _fieldRepo.ListByTableAsync(ParentTableId, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AppField>)[DeptName]);

        AllowAll();
        SetChildReferenceValue(ParentRowId);
        _recordRepo.GetByPublicIdAsync(Parent, Arg.Any<IReadOnlyList<AppField>>(), ParentRecordId, Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(Row(ParentRecordId));
        _recordRepo.GetRowsByIdsAsync(Parent, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [ParentRowId] = new Dictionary<string, object?> { ["Id"] = ParentRowId, ["PublicId"] = ParentRecordId },
            });

        _formulaProjector.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.Arg<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
                .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());
        _relationalProjector.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<IReadOnlyDictionary<long, object?>>)ci.Arg<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
                .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());

        var getRecord = new GetRecordQueryHandler(_tableRepo, _fieldRepo, _recordRepo, _enforcer,
            Substitute.For<IUserRepository>(), _formulaProjector, _relationalProjector);
        _handler = new GetParentRecordQueryHandler(_relRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, getRecord);
    }

    private static Dictionary<string, object?> Row(Guid publicId) => new()
    {
        ["PublicId"] = publicId,
        ["CreatedOn"] = DateTime.UtcNow,
    };

    private void SetChildReferenceValue(long? parentRowId)
    {
        var row = Row(ChildRecordId);
        row[PhysicalNaming.ColumnName(ReferenceFid)] = parentRowId;
        _recordRepo.GetByPublicIdAsync(Child, Arg.Any<IReadOnlyList<AppField>>(), ChildRecordId, Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(row);
    }

    private void AllowAll() =>
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableAccessContext { Unrestricted = true, VisibleFields = ci.Arg<IReadOnlyList<AppField>>() });

    private void SetAccess(AppTable table, TableAccessContext access) =>
        _enforcer.GetTableAccessAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>()).Returns(access);

    private static TableAccessContext NoView() =>
        new() { Unrestricted = false, ViewScope = RecordScopes.None, ModifyScope = RecordScopes.None, VisibleFields = [] };

    [Fact]
    public async Task Handle_VisibleChildAndParent_ReturnsParentLink()
    {
        var link = await _handler.HandleAsync(Child.PublicId, RelPublicId, ChildRecordId);

        link.Should().Be(new ParentRecordLink(Parent.PublicId, ParentRecordId));
    }

    [Fact]
    public async Task Handle_ReferenceHiddenFromUser_ReturnsNull()
    {
        SetAccess(Child, new TableAccessContext { Unrestricted = false, VisibleFields = [] });

        var link = await _handler.HandleAsync(Child.PublicId, RelPublicId, ChildRecordId);

        link.Should().BeNull();
    }

    [Fact]
    public async Task Handle_RecordWithoutParent_ReturnsNull()
    {
        SetChildReferenceValue(null);

        var link = await _handler.HandleAsync(Child.PublicId, RelPublicId, ChildRecordId);

        link.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ParentNotViewable_ReturnsNullWithoutRevealingIt()
    {
        SetAccess(Parent, NoView());

        var link = await _handler.HandleAsync(Child.PublicId, RelPublicId, ChildRecordId);

        link.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ChildNotViewable_ThrowsNotFound()
    {
        SetAccess(Child, NoView());

        var act = () => _handler.HandleAsync(Child.PublicId, RelPublicId, ChildRecordId);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Handle_RouteTableIsNotTheRelationshipsChild_ThrowsNotFound()
    {
        var act = () => _handler.HandleAsync(Parent.PublicId, RelPublicId, ChildRecordId);

        await act.Should().ThrowAsync<NotFoundException>();
        await _recordRepo.DidNotReceiveWithAnyArgs().GetByPublicIdAsync(default!, default!, default);
    }
}
