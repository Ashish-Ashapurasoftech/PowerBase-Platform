using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Fields.Queries.GetFieldUsage;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.FieldReferences;

/// <summary>The Usage tab's report rows: who owns each report, and whether it is a personal (owner-only) or a common one.</summary>
public class GetFieldUsageReportOwnerTests
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

    public GetFieldUsageReportOwnerTests()
    {
        var table = new AppTable { Id = TableId, PublicId = _tablePublicId, AppId = AppId };
        _tables.GetByPublicIdAsync(_tablePublicId, Arg.Any<CancellationToken>()).Returns(table);
        _tables.ListByAppAsync(AppId, Arg.Any<CancellationToken>()).Returns(new List<AppTable> { table });
        _fields.GetByPublicIdAsync(_fieldPublicId, Arg.Any<CancellationToken>())
            .Returns(new AppField { Id = FieldId, Fid = 1, AppTableId = TableId, Name = "Qty" });
        _forms.ListByTableAsync(_tablePublicId, Arg.Any<CancellationToken>()).Returns(new List<Form>());
        _fields.GetRoleUsageAsync(FieldId, AppId, Arg.Any<CancellationToken>()).Returns(new List<FieldUsageRoleItem>());
    }

    private GetFieldUsageQueryHandler NewHandler() => new(_tables, _fields, _forms, _references, _indexer, _users);

    private static FieldReferenceItem ReportRef(Guid id, string name, long ownerId, string visibility, string usage = FieldReferenceUsages.Column) =>
        new(FieldReferenceSourceTypes.Report, 1, id, name, TableId, Guid.NewGuid(), "Orders", null, null, usage, ownerId, visibility);

    private void GivenReferences(params FieldReferenceItem[] items) =>
        _references.ListByTargetFieldAsync(FieldId, Arg.Any<CancellationToken>()).Returns(items);

    [Fact]
    public async Task A_personal_report_is_flagged_personal_and_carries_its_owners_name()
    {
        var reportId = Guid.NewGuid();
        GivenReferences(ReportRef(reportId, "My orders", ownerId: 11, visibility: "Personal"));
        _users.GetNamesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [11] = "Asha Patel" });

        var usage = await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        var report = usage.Reports.Should().ContainSingle().Subject;
        report.Id.Should().Be(reportId);
        report.IsPersonal.Should().BeTrue();
        report.OwnerName.Should().Be("Asha Patel");
    }

    [Theory]
    [InlineData("Shared")]
    [InlineData("MyRole")]
    [InlineData("SpecificRoles")]
    [InlineData("Hidden")]
    public async Task Every_visibility_other_than_personal_is_a_common_report(string visibility)
    {
        GivenReferences(ReportRef(Guid.NewGuid(), "All orders", ownerId: 12, visibility));
        _users.GetNamesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [12] = "Ravi Shah" });

        var usage = await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        usage.Reports.Single().IsPersonal.Should().BeFalse();
    }

    [Fact]
    public async Task One_report_using_the_field_two_ways_is_still_one_row()
    {
        var reportId = Guid.NewGuid();
        GivenReferences(
            ReportRef(reportId, "Orders", ownerId: 11, visibility: "Shared", usage: FieldReferenceUsages.Column),
            ReportRef(reportId, "Orders", ownerId: 11, visibility: "Shared", usage: FieldReferenceUsages.Filter));
        _users.GetNamesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [11] = "Asha Patel" });

        var usage = await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        usage.Reports.Should().ContainSingle().Which.UsedAs.Should().BeEquivalentTo(["column", "filter"]);
    }

    [Fact]
    public async Task A_failed_owner_lookup_leaves_the_owner_blank_but_still_lists_the_report()
    {
        GivenReferences(ReportRef(Guid.NewGuid(), "Orders", ownerId: 11, visibility: "Shared"));
        _users.GetNamesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("user store unavailable"));

        var usage = await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        var report = usage.Reports.Should().ContainSingle().Subject;
        report.OwnerName.Should().BeNull();
        report.Name.Should().Be("Orders");
    }

    [Fact]
    public async Task No_report_references_means_no_owner_lookup_at_all()
    {
        GivenReferences();

        var usage = await NewHandler().HandleAsync(new GetFieldUsageQuery(_tablePublicId, _fieldPublicId));

        usage.Reports.Should().BeEmpty();
        await _users.DidNotReceiveWithAnyArgs().GetNamesByIdsAsync(default!, default);
    }
}
