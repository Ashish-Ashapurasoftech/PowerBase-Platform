using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// Two relationships between the same parent and child (Departments → Employees twice) each make their own
/// "Employees records" report link. Each link belongs to the relationship that made it — not to both because it targets
/// the same child table. A link no live relationship made still shows under every relationship to the table it targets.
/// </summary>
public class RelationshipReportLinkOwnershipTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly RelationshipQueriesHandler _queries;

    private const long DeptId = 6, EmpId = 7, Rel1 = 913, Rel2 = 914;
    private static readonly Guid DeptPublicId = Guid.NewGuid(), EmpPublicId = Guid.NewGuid();
    private static readonly Guid Rel1PublicId = Guid.NewGuid(), Rel2PublicId = Guid.NewGuid();

    private static readonly Relationship First = new() { Id = Rel1, PublicId = Rel1PublicId, ParentTableId = DeptId, ChildTableId = EmpId, ReferenceFieldId = 100, ReferenceFid = 10 };
    private static readonly Relationship Second = new() { Id = Rel2, PublicId = Rel2PublicId, ParentTableId = DeptId, ChildTableId = EmpId, ReferenceFieldId = 120, ReferenceFid = 12 };

    public RelationshipReportLinkOwnershipTests()
    {
        _queries = new RelationshipQueriesHandler(_appRepo, _tableRepo, _fieldRepo, _relRepo);
        _tableRepo.GetByIdAsync(DeptId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = DeptId, PublicId = DeptPublicId, Name = "Departments" });
        _tableRepo.GetByIdAsync(EmpId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = EmpId, PublicId = EmpPublicId, Name = "Employees" });
        _relRepo.GetByPublicIdAsync(Rel1PublicId, Arg.Any<CancellationToken>()).Returns(First);
        _relRepo.GetByPublicIdAsync(Rel2PublicId, Arg.Any<CancellationToken>()).Returns(Second);
        _relRepo.ListByParentTableAsync(DeptId, Arg.Any<CancellationToken>()).Returns(new List<Relationship> { First, Second });
        _fieldRepo.ListByTableAsync(EmpId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 100, Fid = 10, AppTableId = EmpId, Name = "Related Department", TypeCode = "Reference", Settings = """{"relationshipId":913}""" },
            new() { Id = 120, Fid = 12, AppTableId = EmpId, Name = "Department Name", TypeCode = "Reference", Settings = """{"relationshipId":914}""" },
        });
        _fieldRepo.ListByTableAsync(DeptId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 3, Fid = 3, AppTableId = DeptId, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
            ReportLink(30, "Employees records", Rel1),
            ReportLink(31, "Employees records 1", Rel2),
            ReportLink(32, "All employees (made by hand)", null),
            ReportLink(33, "Old link (its relationship is gone)", 999),
        });
    }

    private static AppField ReportLink(long id, string label, long? relationshipId) => new()
    {
        Id = id, PublicId = Guid.NewGuid(), Fid = (int)id, AppTableId = DeptId, Name = label, Label = label, TypeCode = "ReportLink",
        Settings = relationshipId is null
            ? $$"""{"targetTablePublicId":"{{EmpPublicId}}"}"""
            : $$"""{"relationshipId":{{relationshipId}},"targetTablePublicId":"{{EmpPublicId}}"}""",
    };

    private async Task<string[]> ReportLinksOf(Guid relPublicId) =>
        (await _queries.GetAsync(relPublicId)).Fields.Where(f => f.Role == "reportlink").Select(f => f.Name).ToArray();

    [Fact]
    public async Task EachRelationship_ShowsItsOwnReportLink_NotTheOthers()
    {
        (await ReportLinksOf(Rel1PublicId)).Should().Contain("Employees records").And.NotContain("Employees records 1");
        (await ReportLinksOf(Rel2PublicId)).Should().Contain("Employees records 1").And.NotContain("Employees records");
    }

    [Fact]
    public async Task ALinkOfNoLiveRelationship_StillShowsUnderEveryRelationshipToItsTable()
    {
        (await ReportLinksOf(Rel1PublicId)).Should().Contain(["All employees (made by hand)", "Old link (its relationship is gone)"]);
        (await ReportLinksOf(Rel2PublicId)).Should().Contain(["All employees (made by hand)", "Old link (its relationship is gone)"]);
    }

    [Fact]
    public async Task ListByTable_AlsoKeepsEachLinkWithItsOwnRelationship()
    {
        _tableRepo.GetByPublicIdAsync(DeptPublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = DeptId, PublicId = DeptPublicId, Name = "Departments" });
        _relRepo.ListByTableAsync(DeptId, Arg.Any<CancellationToken>()).Returns(new List<Relationship> { First, Second });

        var all = await _queries.ByTableAsync(DeptPublicId);

        all.Single(r => r.PublicId == Rel1PublicId).Fields.Should().NotContain(f => f.Name == "Employees records 1");
        all.Single(r => r.PublicId == Rel2PublicId).Fields.Should().NotContain(f => f.Name == "Employees records");
    }
}
