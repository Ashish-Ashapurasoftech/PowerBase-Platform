using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Fields.Queries.GetFieldUsage;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.FieldReferences;

/// <summary>How the Usage tab reports relationships, dashboards and custom table rules.</summary>
public class GetFieldUsageOtherItemsTests
{
    private const long AppId = 3;
    private const long TableId = 7;
    private const long FieldId = 1001;

    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IFormRepository _forms = Substitute.For<IFormRepository>();
    private readonly IFieldReferenceRepository _references = Substitute.For<IFieldReferenceRepository>();
    private readonly IFieldReferenceIndexer _indexer = Substitute.For<IFieldReferenceIndexer>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly Guid _tablePublicId = Guid.NewGuid();
    private readonly Guid _fieldPublicId = Guid.NewGuid();
    private readonly Guid _childTablePublicId = Guid.NewGuid();

    public GetFieldUsageOtherItemsTests()
    {
        var table = new AppTable { Id = TableId, PublicId = _tablePublicId, AppId = AppId };
        _tables.GetByPublicIdAsync(_tablePublicId, Arg.Any<CancellationToken>()).Returns(table);
        _tables.ListByAppAsync(AppId, Arg.Any<CancellationToken>()).Returns(new List<AppTable> { table, new() { Id = 8, AppId = AppId } });
        _fields.GetByPublicIdAsync(_fieldPublicId, Arg.Any<CancellationToken>())
            .Returns(new AppField { Id = FieldId, Fid = 1, AppTableId = TableId, Name = "Qty" });
        _forms.ListByTableAsync(_tablePublicId, Arg.Any<CancellationToken>()).Returns(new List<Form>());
        _fields.GetRoleUsageAsync(FieldId, AppId, Arg.Any<CancellationToken>()).Returns(new List<FieldUsageRoleItem>());
    }

    private GetFieldUsageQueryHandler NewHandler() => new(_tables, _fields, _forms, _references, _indexer, _users);

    private FieldReferenceItem Ref(string sourceType, Guid id, string name, string usage, long? ownerId = null) =>
        new(sourceType, 1, id, name, TableId, _childTablePublicId, "Folders", null, null, usage, ownerId, null);

    private void GivenReferences(params FieldReferenceItem[] items) =>
        _references.ListByTargetFieldAsync(FieldId, Arg.Any<CancellationToken>()).Returns(items);

    [Fact]
    public async Task A_relationship_is_listed_by_its_name_with_the_ways_it_uses_the_field()
    {
        var id = Guid.NewGuid();
        GivenReferences(
            Ref(FieldReferenceSourceTypes.Relationship, id, "Testing ◂ Folders", FieldReferenceUsages.RelationshipReference),
            Ref(FieldReferenceSourceTypes.Relationship, id, "Testing ◂ Folders", FieldReferenceUsages.RelationshipDisplayLabel));

        var usage = await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        var item = usage.OtherItems.Should().ContainSingle().Subject;
        item.Kind.Should().Be(FieldUsageKinds.Relationship);
        item.Id.Should().Be(id);
        item.Name.Should().Be("Testing ◂ Folders");
        item.UsedAs.Should().BeEquivalentTo(["display label", "reference field"]);
        item.TableId.Should().Be(_childTablePublicId);
        item.TableName.Should().Be("Folders");
    }

    [Fact]
    public async Task A_dashboard_carries_its_owners_name()
    {
        GivenReferences(Ref(FieldReferenceSourceTypes.Dashboard, Guid.NewGuid(), "Sales", FieldReferenceUsages.DashboardFilter, ownerId: 11));
        _users.GetNamesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [11] = "Asha Patel" });

        var usage = await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        var item = usage.OtherItems.Should().ContainSingle().Subject;
        item.Kind.Should().Be(FieldUsageKinds.Dashboard);
        item.Owner.Should().Be("Asha Patel");
        item.UsedAs.Should().BeEquivalentTo(["dashboard filter"]);
    }

    [Fact]
    public async Task A_custom_rule_is_listed_under_its_role_with_view_and_modify_kept_apart()
    {
        var roleId = Guid.NewGuid();
        GivenReferences(
            Ref(FieldReferenceSourceTypes.CustomRule, roleId, "Sales Rep", FieldReferenceUsages.ViewRule),
            Ref(FieldReferenceSourceTypes.CustomRule, roleId, "Sales Rep", FieldReferenceUsages.ModifyRule));

        var usage = await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        var item = usage.OtherItems.Should().ContainSingle().Subject;
        item.Kind.Should().Be(FieldUsageKinds.CustomRule);
        item.Id.Should().Be(roleId);
        item.Name.Should().Be("Sales Rep");
        item.UsedAs.Should().BeEquivalentTo(["modify rule", "view rule"]);
    }

    [Fact]
    public async Task The_newer_kinds_do_not_leak_into_the_older_lists()
    {
        GivenReferences(
            Ref(FieldReferenceSourceTypes.Relationship, Guid.NewGuid(), "A ◂ B", FieldReferenceUsages.RelationshipReference),
            Ref(FieldReferenceSourceTypes.CustomRule, Guid.NewGuid(), "Role", FieldReferenceUsages.ViewRule));

        var usage = await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        usage.Reports.Should().BeEmpty();
        usage.Fields.Should().BeEmpty();
        usage.FormRules.Should().BeEmpty();
        usage.OtherItems.Should().HaveCount(2);
    }

    [Fact]
    public async Task All_of_the_apps_tables_are_checked_for_a_backfill_in_one_call()
    {
        GivenReferences();

        await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        await _indexer.Received(1).EnsureTablesIndexedAsync(
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { TableId, 8 })), Arg.Any<CancellationToken>());
        await _indexer.DidNotReceiveWithAnyArgs().EnsureTableIndexedAsync(default, default);
    }
}
