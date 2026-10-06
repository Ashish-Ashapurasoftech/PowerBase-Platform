using FluentAssertions;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

public class EncryptedRowFilterTests
{
    private static readonly List<AppField> Fields =
    [
        new() { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
        new() { Id = 6, Fid = 6, Name = "Name", TypeCode = "Text", IsEncrypted = true },
        new() { Id = 7, Fid = 7, Name = "Code", TypeCode = "Text" },
    ];

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows() =>
    [
        new Dictionary<string, object?> { ["Id"] = 1L, ["f_6"] = "HR", ["f_7"] = "a" },
        new Dictionary<string, object?> { ["Id"] = 2L, ["f_6"] = "Finance", ["f_7"] = "b" },
        new Dictionary<string, object?> { ["Id"] = 3L, ["f_6"] = "hr ops", ["f_7"] = "b" },
    ];

    private static FilterGroup Tree(string logic, params (long fid, string op, string value)[] c) => new()
    {
        Logic = logic,
        Nodes = c.Select(x => new FilterNode { Condition = new FilterCondition { FieldId = x.fid, Operator = x.op, Value = x.value } }).ToList(),
    };

    [Fact]
    public void Equals_OnEncryptedField_MatchesDecryptedValue() =>
        EncryptedRowFilter.MatchingIds(Rows(), Fields, Tree("and", (6, "eq", "HR"))).Should().Equal(1L);

    [Fact]
    public void Contains_AndOr_AcrossEncryptedAndPlainFields() =>
        EncryptedRowFilter.MatchingIds(Rows(), Fields, Tree("or", (6, "contains", "hr"), (7, "eq", "b"))).Should().Equal(1L, 2L, 3L);

    [Fact]
    public void In_OnEncryptedField() =>
        EncryptedRowFilter.MatchingIds(Rows(), Fields, Tree("and", (6, "in", "[\"HR\",\"Finance\"]"))).Should().Equal(1L, 2L);

    [Fact]
    public void Formula_Field_IsJudgedByItsComputedValue_AsItsResultType()
    {
        var fields = new List<AppField>(Fields) { new() { Id = 9, Fid = 9, Name = "Double", TypeCode = "Formula_Number" } };
        IReadOnlyList<IReadOnlyDictionary<long, object?>> computed =
        [
            new Dictionary<long, object?> { [9] = 10m },
            new Dictionary<long, object?> { [9] = 9m },
            new Dictionary<long, object?> { [9] = 100m },
        ];

        // As a number, 9 < 10 < 100 (as text "100" would sort before "9").
        EncryptedRowFilter.MatchingIds(Rows(), fields, Tree("and", (9, "gt", "9.5")), computed).Should().Equal(1L, 3L);
    }

    [Fact]
    public void TouchesEncrypted_FollowsFieldFlagOrApp()
    {
        EncryptedRowFilter.TouchesEncrypted(Tree("and", (6, "eq", "x")), Fields, false).Should().BeTrue();
        EncryptedRowFilter.TouchesEncrypted(Tree("and", (7, "eq", "x")), Fields, false).Should().BeFalse();
        EncryptedRowFilter.TouchesEncrypted(Tree("and", (7, "eq", "x")), Fields, true).Should().BeTrue();
        EncryptedRowFilter.TouchesEncrypted(Tree("and", (3, "eq", "1")), Fields, true).Should().BeFalse();   // system field
    }
}
