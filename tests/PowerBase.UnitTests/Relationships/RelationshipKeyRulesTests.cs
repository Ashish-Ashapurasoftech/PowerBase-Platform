using FluentAssertions;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

/// <summary>Which parent fields are keys and so must stay unique: the table's key field (always), and any field a
/// relationship uses as its key — its own display-key override, else the table's key field.</summary>
public class RelationshipKeyRulesTests
{
    private const long DepartmentTableId = 10;
    private const long EmployeeTableId = 20;

    private static readonly AppField DeptName = new() { Id = 101, Fid = 6, Name = "Department Name" };
    private static readonly AppField DeptCode = new() { Id = 102, Fid = 7, Name = "Department Code" };
    private static readonly AppField Budget = new() { Id = 103, Fid = 8, Name = "Budget" };

    private static AppTable Department(long? keyFieldId) => new() { Id = DepartmentTableId, Name = "Department", KeyFieldId = keyFieldId };

    private static Relationship EmployeesToDepartment(long? displayKeyFieldId = null) =>
        new() { Id = 1, ParentTableId = DepartmentTableId, ChildTableId = EmployeeTableId, DisplayKeyFieldId = displayKeyFieldId };

    [Fact]
    public void TableKey_UsedByStandardRelationship_IsKeyUsageAndLocked()
    {
        var table = Department(DeptName.Id);
        var rels = new[] { EmployeesToDepartment() };

        RelationshipKeyRules.KeyUsages(table, DeptName, rels).Should().ContainSingle();
        RelationshipKeyRules.IsUniqueLocked(table, DeptName, rels).Should().BeTrue();
    }

    [Fact]
    public void TableKey_WithNoRelationship_IsLockedButHasNoUsage()
    {
        var table = Department(DeptName.Id);

        RelationshipKeyRules.KeyUsages(table, DeptName, []).Should().BeEmpty();
        RelationshipKeyRules.IsUniqueLocked(table, DeptName, []).Should().BeTrue();
    }

    [Fact]
    public void TableKey_WhenTheRelationshipHasItsOwnAlternateKey_IsNotThatRelationshipsKey()
    {
        var table = Department(DeptName.Id);
        var rels = new[] { EmployeesToDepartment(displayKeyFieldId: DeptCode.Id) };

        RelationshipKeyRules.KeyUsages(table, DeptName, rels).Should().BeEmpty();
        RelationshipKeyRules.KeyUsages(table, DeptCode, rels).Should().ContainSingle();
        // Still the table key, so still locked.
        RelationshipKeyRules.IsUniqueLocked(table, DeptName, rels).Should().BeTrue();
    }

    [Fact]
    public void AlternateKey_OfAnyRelationship_IsLocked()
    {
        var table = Department(keyFieldId: null);
        var rels = new[] { EmployeesToDepartment(displayKeyFieldId: DeptCode.Id) };

        RelationshipKeyRules.IsUniqueLocked(table, DeptCode, rels).Should().BeTrue();
    }

    [Fact]
    public void OtherField_IsNotLocked()
    {
        var table = Department(DeptName.Id);
        var rels = new[] { EmployeesToDepartment(displayKeyFieldId: DeptCode.Id) };

        RelationshipKeyRules.KeyUsages(table, Budget, rels).Should().BeEmpty();
        RelationshipKeyRules.IsUniqueLocked(table, Budget, rels).Should().BeFalse();
    }

    [Fact]
    public void TableWhereThisTableIsTheChild_DoesNotMakeItsKeyARelationshipKey()
    {
        // Department is the child here (Department → Company), so Department's key is not this relationship's key.
        var table = Department(DeptName.Id);
        var rels = new[] { new Relationship { Id = 2, ParentTableId = 99, ChildTableId = DepartmentTableId } };

        RelationshipKeyRules.KeyUsages(table, DeptName, rels).Should().BeEmpty();
    }
}
