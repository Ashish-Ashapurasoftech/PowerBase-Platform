using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

/// <summary>
/// KeyFieldResolver is the single source of truth for "what does Standard key actually resolve
/// to" — the relationship wizard, the reference picker, and ReferenceWriteValidator all depend on
/// this exact fallback chain: relationship override -> parent table's Set Key -> null (Record ID#).
/// </summary>
public class KeyFieldResolverTests
{
    private static AppField Field(long id, int fid, string name) =>
        new() { Id = id, Fid = fid, Name = name, Label = name, TypeCode = "Text" };

    private static readonly AppField DeptName = Field(1, 6, "Department Name");
    private static readonly AppField DeptCode = Field(2, 7, "Department Code");

    [Fact]
    public void ResolveDisplayKey_RelationshipOverrideSet_ReturnsOverrideField()
    {
        var rel = new Relationship { DisplayKeyFieldId = DeptCode.Id };
        var parent = new AppTable { KeyFieldId = null };

        var result = KeyFieldResolver.ResolveDisplayKey(rel, parent, [DeptName, DeptCode]);

        result.Should().Be(DeptCode);
    }

    [Fact]
    public void ResolveDisplayKey_NoOverride_FallsBackToParentTableKeyField()
    {
        var rel = new Relationship { DisplayKeyFieldId = null };
        var parent = new AppTable { KeyFieldId = DeptCode.Id };

        var result = KeyFieldResolver.ResolveDisplayKey(rel, parent, [DeptName, DeptCode]);

        result.Should().Be(DeptCode);
    }

    [Fact]
    public void ResolveDisplayKey_OverrideTakesPrecedenceOverParentTableKey()
    {
        // Relationship overrides to DeptName even though the table's own Set Key is DeptCode.
        var rel = new Relationship { DisplayKeyFieldId = DeptName.Id };
        var parent = new AppTable { KeyFieldId = DeptCode.Id };

        var result = KeyFieldResolver.ResolveDisplayKey(rel, parent, [DeptName, DeptCode]);

        result.Should().Be(DeptName);
    }

    [Fact]
    public void ResolveDisplayKey_NeitherSet_ReturnsNull()
    {
        var rel = new Relationship { DisplayKeyFieldId = null };
        var parent = new AppTable { KeyFieldId = null };

        var result = KeyFieldResolver.ResolveDisplayKey(rel, parent, [DeptName, DeptCode]);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveDisplayKey_NullRelationship_FallsBackToParentTableKey()
    {
        // GetParentOptionsQueryHandler-style callers may not have a relationship yet.
        var parent = new AppTable { KeyFieldId = DeptCode.Id };

        var result = KeyFieldResolver.ResolveDisplayKey(null, parent, [DeptName, DeptCode]);

        result.Should().Be(DeptCode);
    }

    [Fact]
    public void ResolveDisplayKey_OverrideFieldIdMissingFromList_FallsBackToParentTableKey()
    {
        // Defensive case: DisplayKeyFieldId points at a field that's since been deleted/renamed away.
        var rel = new Relationship { DisplayKeyFieldId = 99999 };
        var parent = new AppTable { KeyFieldId = DeptCode.Id };

        var result = KeyFieldResolver.ResolveDisplayKey(rel, parent, [DeptName, DeptCode]);

        result.Should().Be(DeptCode);
    }

    [Fact]
    public async Task ResolveAsync_TableHasNoKeyField_ReturnsNull()
    {
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var table = new AppTable { Id = 5, KeyFieldId = null };

        var result = await KeyFieldResolver.ResolveAsync(table, fieldRepo, CancellationToken.None);

        result.Should().BeNull();
        await fieldRepo.DidNotReceiveWithAnyArgs().GetByIdInTableAsync(default, default, default);
    }

    [Fact]
    public async Task ResolveAsync_TableHasKeyField_ReturnsResolvedField()
    {
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var table = new AppTable { Id = 5, KeyFieldId = DeptCode.Id };
        fieldRepo.GetByIdInTableAsync(DeptCode.Id, table.Id, Arg.Any<CancellationToken>()).Returns(DeptCode);

        var result = await KeyFieldResolver.ResolveAsync(table, fieldRepo, CancellationToken.None);

        result.Should().Be(DeptCode);
    }

    [Fact]
    public void ColumnName_NullKeyField_ReturnsId()
    {
        KeyFieldResolver.ColumnName(null).Should().Be("Id");
    }

    [Fact]
    public void ColumnName_KeyFieldWithPhysicalColumn_ReturnsItsColumn()
    {
        var field = Field(2, 7, "Department Code");
        field.PhysicalColumnName = "f_7";

        KeyFieldResolver.ColumnName(field).Should().Be("f_7");
    }

    [Fact]
    public void ConvertToColumnType_NullKeyField_ParsesAsRowId()
    {
        KeyFieldResolver.ConvertToColumnType(null, "42").Should().Be(42L);
    }

    [Fact]
    public void ConvertToColumnType_NullKeyField_NonNumericText_ReturnsNull()
    {
        KeyFieldResolver.ConvertToColumnType(null, "ENG").Should().BeNull();
    }

    [Fact]
    public void ConvertToColumnType_BlankText_ReturnsNull()
    {
        KeyFieldResolver.ConvertToColumnType(DeptCode, "").Should().BeNull();
        KeyFieldResolver.ConvertToColumnType(DeptCode, null).Should().BeNull();
    }

    [Fact]
    public void ConvertToColumnType_NumberKeyField_ParsesDecimal()
    {
        var numberField = new AppField { Id = 3, Fid = 8, Name = "Score", TypeCode = "Number" };

        KeyFieldResolver.ConvertToColumnType(numberField, "12.5").Should().Be(12.5m);
    }

    [Fact]
    public void ConvertToColumnType_TextKeyField_PassesThroughAsIs()
    {
        KeyFieldResolver.ConvertToColumnType(DeptCode, "ENG").Should().Be("ENG");
    }

    [Fact]
    public void FormatForSubmit_Null_ReturnsEmptyString()
    {
        KeyFieldResolver.FormatForSubmit(null).Should().Be(string.Empty);
    }

    [Fact]
    public void FormatForSubmit_DateTime_FormatsAsIsoDate()
    {
        KeyFieldResolver.FormatForSubmit(new DateTime(2026, 9, 16)).Should().Be("2026-09-16");
    }
}
