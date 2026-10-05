using System.Reflection;
using Dapper;
using FluentAssertions;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.UnitTests.Records;

/// <summary>The SQL a report filter / summary matching criteria turns into, for the two cases that
/// used to read differently from what the user sees: text-style operators on numbers, and plain
/// dates against Date &amp; Time columns.</summary>
public class RecordFilterSqlTests
{
    private static readonly MethodInfo BuildWhere = typeof(RecordRepository)
        .GetMethod("BuildFilterTreeWhere", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly Dictionary<long, AppField> Fields = new()
    {
        [1] = new AppField { Id = 1001, Fid = 1, Name = "Date Created", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "CreatedOn" },
        [3] = new AppField { Id = 1003, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" },
        [7] = new AppField { Id = 1007, Fid = 7, Name = "Salary", TypeCode = "Currency", PhysicalColumnName = "f_7" },
        [8] = new AppField { Id = 1008, Fid = 8, Name = "Experience", TypeCode = "Number", PhysicalColumnName = "f_8" },
        [9] = new AppField { Id = 1009, Fid = 9, Name = "Joining Date", TypeCode = "Date", PhysicalColumnName = "f_9" },
        [12] = new AppField { Id = 1012, Fid = 12, Name = "Meeting At", TypeCode = "DateTime", PhysicalColumnName = "f_12" },
    };

    private static (string Sql, DynamicParameters Params) Where(long fieldId, string op, string? value)
    {
        var group = new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fieldId, Operator = op, Value = value } }] };
        var parameters = new DynamicParameters();
        var sql = (string)BuildWhere.Invoke(null, [group, parameters, Fields, null])!;
        return (sql, parameters);
    }

    // ── Text-style operators on numbers ──

    [Theory]
    [InlineData("contains")]
    [InlineData("notContains")]
    [InlineData("startsWith")]
    [InlineData("notStartsWith")]
    [InlineData("wildcard")]
    [InlineData("notWildcard")]
    public void TextOperatorOnDecimalColumn_ReadsTheNumberAsShown_NotItsStorageForm(string op)
    {
        var (sql, _) = Where(8, op, "5");

        // Trailing zeros (and a trailing point) are trimmed before matching: "5.0000" reads "5".
        sql.Should().Contain("REPLACE(RTRIM(REPLACE(CAST(f_8 AS NVARCHAR(50)), '0', ' ')), ' ', '0')");
        sql.Should().NotContain("CAST(f_8 AS NVARCHAR(MAX))");
    }

    [Fact]
    public void WildcardOnNumber_KeepsTheUsersPattern()
    {
        var (sql, p) = Where(8, "wildcard", "1?");

        sql.Should().Contain("LIKE @fv0");
        p.Get<string>("fv0").Should().Be("1_");
    }

    [Fact]
    public void NumericComparison_StaysNumeric()
    {
        var (sql, p) = Where(7, "gt", "49999.5");

        sql.Should().Be(" AND (f_7 > @fv0)");
        p.Get<decimal>("fv0").Should().Be(49999.5m);
    }

    [Fact]
    public void TextOperatorOnIntegerColumn_IsUnchangedByTheTrim()
    {
        var (sql, _) = Where(3, "contains", "1");

        // Record ID# has no decimal point, so the CHARINDEX branch returns it as-is.
        sql.Should().Contain("WHEN CHARINDEX('.', CAST(Id AS NVARCHAR(50))) = 0 THEN CAST(Id AS NVARCHAR(50))");
    }

    // ── Plain dates against Date & Time columns ──

    [Theory]
    [InlineData(1L, "CreatedOn")]
    [InlineData(12L, "f_12")]
    public void DateTimeColumn_AfterADate_MeansFromTheNextDay(long fieldId, string col)
    {
        var (sql, p) = Where(fieldId, "gt", "2026-09-28");

        sql.Should().Be($" AND ({col} >= @fv0next)");
        p.Get<DateTime>("fv0next").Should().Be(new DateTime(2026, 9, 29));
    }

    [Theory]
    [InlineData("gte", "CreatedOn >= @fv0")]
    [InlineData("lt", "CreatedOn < @fv0")]
    [InlineData("lte", "CreatedOn < @fv0next")]
    [InlineData("eq", "(CreatedOn >= @fv0 AND CreatedOn < @fv0next)")]
    [InlineData("ne", "(CreatedOn < @fv0 OR CreatedOn >= @fv0next)")]
    public void DateTimeColumn_ComparesWholeDays(string op, string expected)
    {
        var (sql, p) = Where(1, op, "2026-09-28");

        sql.Should().Be($" AND ({expected})");
        p.Get<DateTime>("fv0").Should().Be(new DateTime(2026, 9, 28));
        p.Get<DateTime>("fv0next").Should().Be(new DateTime(2026, 9, 29));
    }

    [Fact]
    public void DateTimeColumn_WithATimeOfDay_ComparesExactly()
    {
        var (sql, _) = Where(1, "gt", "2026-09-28T10:30:00");

        sql.Should().Be(" AND (CreatedOn > @fv0)");
    }

    [Fact]
    public void DateOnlyColumn_IsUnchanged()
    {
        var (sql, _) = Where(9, "lte", "2026-09-28");

        sql.Should().Be(" AND (f_9 <= @fv0)");
    }

    [Fact]
    public void DuringTheCurrentDay_OnDateTime_CoversTheWholeDay()
    {
        // "during" reaches the SQL builder already expanded to gte/lte of the period's dates.
        var group = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new FilterNode { Condition = new FilterCondition { FieldId = 1, Operator = "gte", Value = "2026-09-28" } },
                new FilterNode { Condition = new FilterCondition { FieldId = 1, Operator = "lte", Value = "2026-09-28" } },
            ],
        };
        var p = new DynamicParameters();
        var sql = (string)BuildWhere.Invoke(null, [group, p, Fields, null])!;

        sql.Should().Be(" AND (CreatedOn >= @fv0 AND CreatedOn < @fv1next)");
        p.Get<DateTime>("fv1next").Should().Be(new DateTime(2026, 9, 29));
    }

    // ── "the value in the parent's field" (Summary matching criteria) ──

    // Parent table 5: Start Date (fid 6), End Date (fid 7), Budget Left (formula, fid 8). The child's
    // reference field is fid 10.
    private static readonly ParentFieldScope Parent = new(5, 10, new Dictionary<long, AppField>
    {
        [3] = new AppField { Id = 2003, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" },
        [6] = new AppField { Id = 2006, Fid = 6, Name = "Start Date", TypeCode = "Date", PhysicalColumnName = "f_6" },
        [7] = new AppField { Id = 2007, Fid = 7, Name = "End Date", TypeCode = "Date", PhysicalColumnName = "f_7" },
        [8] = new AppField { Id = 2008, Fid = 8, Name = "Budget Left", TypeCode = "Formula_Number" },
    });

    private static FilterNode ParentCond(long fieldId, string op, long parentFid) =>
        new() { Condition = new FilterCondition { FieldId = fieldId, Operator = op, ValueMode = "parentField", ValueFieldId = parentFid } };

    private static (string Sql, DynamicParameters Params) WhereWithParent(ParentFieldScope? scope, params FilterNode[] nodes)
    {
        var group = new FilterGroup { Logic = "and", Nodes = [.. nodes] };
        var parameters = new DynamicParameters();
        var sql = (string)BuildWhere.Invoke(null, [group, parameters, Fields, scope])!;
        return (sql, parameters);
    }

    [Fact]
    public void ParentField_BetweenTwoParentDates_ReadsEachChildsOwnParent()
    {
        var (sql, p) = WhereWithParent(Parent, ParentCond(9, "gte", 6), ParentCond(9, "lte", 7));

        sql.Should().Be(" AND ("
            + "f_9 >= (SELECT p.f_6 FROM data.t_5 p WHERE p.Id = c.f_10) AND "
            + "f_9 <= (SELECT p.f_7 FROM data.t_5 p WHERE p.Id = c.f_10))");
        p.ParameterNames.Should().BeEmpty();
    }

    [Fact]
    public void ParentField_SystemField_UsesItsColumn()
    {
        var (sql, _) = WhereWithParent(Parent, ParentCond(3, "eq", 3));

        sql.Should().Be(" AND (Id = (SELECT p.Id FROM data.t_5 p WHERE p.Id = c.f_10))");
    }

    [Fact]
    public void ParentField_DateEquals_ComparesWholeDays()
    {
        var (sql, _) = WhereWithParent(Parent, ParentCond(12, "date_eq", 6));

        sql.Should().Be(" AND (CAST(f_12 AS DATE) = CAST((SELECT p.f_6 FROM data.t_5 p WHERE p.Id = c.f_10) AS DATE))");
    }

    [Fact]
    public void ParentField_KeepsParameterNumberingForTheConditionsAfterIt()
    {
        var (sql, p) = WhereWithParent(Parent, ParentCond(9, "gte", 6), new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "gt", Value = "10" } });

        sql.Should().EndWith("f_7 > @fv0)");
        p.Get<long>("fv0").Should().Be(10L);
    }

    [Fact]
    public void ParentField_WithoutAParentScope_IsANoOp()
    {
        // Outside a summary query there's no parent to read — the condition is dropped, not guessed.
        var (sql, _) = WhereWithParent(null, ParentCond(9, "gte", 6));

        sql.Should().BeEmpty();
    }

    [Theory]
    [InlineData(8L)]    // calculated — no column
    [InlineData(99L)]   // not on the parent table
    public void ParentField_WithoutAColumnToRead_IsANoOp(long parentFid)
    {
        var (sql, _) = WhereWithParent(Parent, ParentCond(9, "gte", parentFid));

        sql.Should().BeEmpty();
    }
}
