using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

public class ParentDeleteGuardTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();

    [Fact]
    public async Task Throws_when_children_reference_the_parent()
    {
        var parent = new AppTable { Id = 5, Name = "Product" };
        var rels = new List<Relationship> { new() { ChildTableId = 77, ReferenceFid = 10 } };
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, Name = "Invoice Item" });
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = 2 });

        var act = () => ParentDeleteGuard.EnsureNotReferencedAsync(parent, rels, new[] { 1L }, _tableRepo, _recordRepo, default);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Does_not_throw_when_no_children_reference_the_parent()
    {
        var parent = new AppTable { Id = 5, Name = "Product" };
        var rels = new List<Relationship> { new() { ChildTableId = 77, ReferenceFid = 10 } };
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, Name = "Invoice Item" });
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null,
                Arg.Any<IReadOnlyCollection<object>>(), Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?>());

        var act = () => ParentDeleteGuard.EnsureNotReferencedAsync(parent, rels, new[] { 1L }, _tableRepo, _recordRepo, default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Set_key_parent_matches_children_on_row_ids_and_throws_when_referenced()
    {
        // Departments keyed by "Department Name": the child's reference column still stores the
        // parent row Id, so the guard must query with the Ids, never the key field's text values.
        var parent = new AppTable { Id = 5, Name = "Department", KeyFieldId = 900 };
        var rels = new List<Relationship> { new() { ChildTableId = 77, ReferenceFid = 10 } };
        _tableRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 77, Name = "Employee" });
        _recordRepo.AggregateByReferenceAsync(Arg.Any<AppTable>(), 10, "Count", null,
                Arg.Is<IReadOnlyCollection<object>>(k => k.SequenceEqual(new object[] { 1L, 2L })),
                Arg.Any<Application.Reports.FilterGroup?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<long, AppField>?>(), Arg.Any<ParentFieldScope?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<object, object?> { [1L] = 3 });

        var act = () => ParentDeleteGuard.EnsureNotReferencedAsync(parent, rels, new[] { 1L, 2L }, _tableRepo, _recordRepo, default);

        await act.Should().ThrowAsync<ConflictException>().WithMessage("Cannot delete: 3 Employee record(s)*");
        await _recordRepo.DidNotReceiveWithAnyArgs().GetColumnValuesByIdsAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task No_relationships_is_a_noop()
    {
        var parent = new AppTable { Id = 5, Name = "Product" };
        var act = () => ParentDeleteGuard.EnsureNotReferencedAsync(parent, new List<Relationship>(), new[] { 1L }, _tableRepo, _recordRepo, default);
        await act.Should().NotThrowAsync();
    }
}
