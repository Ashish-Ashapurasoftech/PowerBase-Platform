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
        [20] = new AppField { Id = 1020, Fid = 20, Name = "Title", TypeCode = "Text", PhysicalColumnName = "f_20" },
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

    [Fact]
    public void ParentField_NotOnTheParentTable_IsANoOp()
    {
        var (sql, _) = WhereWithParent(Parent, ParentCond(9, "gte", 99));

        sql.Should().BeEmpty();
    }

    // ── … a parent Lookup / Summary / Formula: no column, its values worked out per parent ──

    [Fact]
    public void ParentField_Calculated_ComparesEachChildToItsOwnParentsValue()
    {
        var scope = Parent with
        {
            ComputedValues = new Dictionary<long, IReadOnlyDictionary<long, object?>>
            {
                [8] = new Dictionary<long, object?> { [1] = 50000m, [2] = null, [3] = 72000m },
            },
        };

        var (sql, p) = WhereWithParent(scope, ParentCond(7, "gt", 8));

        // Parent 2 has no value: left out, so its children match nothing (like a blank stored value).
        sql.Should().Be(" AND (f_7 > (SELECT pv.v FROM (VALUES (1, @fv0), (3, @fv1)) pv(k, v) WHERE pv.k = c.f_10))");
        p.Get<decimal>("fv0").Should().Be(50000m);
        p.Get<decimal>("fv1").Should().Be(72000m);
    }

    [Fact]
    public void ParentField_Calculated_KeepsParameterNumberingForTheConditionsAfterIt()
    {
        var scope = Parent with
        {
            ComputedValues = new Dictionary<long, IReadOnlyDictionary<long, object?>> { [8] = new Dictionary<long, object?> { [1] = 5m } },
        };

        var (sql, p) = WhereWithParent(scope, ParentCond(7, "gt", 8), new FilterNode { Condition = new FilterCondition { FieldId = 8, Operator = "gt", Value = "10" } });

        sql.Should().EndWith("f_8 > @fv1)");
        p.Get<decimal>("fv0").Should().Be(5m);
        p.Get<long>("fv1").Should().Be(10L);
    }

    [Fact]
    public void ParentField_ReferenceComparedAsItsKey_ComparesEachChildToItsParentsKeyValue()
    {
        // The parent's "Related Region" (fid 9, a Reference converted from a Text field) holds a Record ID#; the
        // projector hands over the key it shows instead.
        var scope = new ParentFieldScope(5, 10, new Dictionary<long, AppField>
        {
            [9] = new AppField { Id = 2009, Fid = 9, Name = "Related Region", TypeCode = "Reference", PhysicalColumnName = "f_9" },
        })
        {
            ComputedValues = new Dictionary<long, IReadOnlyDictionary<long, object?>> { [9] = new Dictionary<long, object?> { [1] = "West" } },
        };
        var fields = new Dictionary<long, AppField>(Fields) { [20] = new AppField { Id = 1020, Fid = 20, Name = "Region", TypeCode = "Text", PhysicalColumnName = "f_20" } };
        var group = new FilterGroup { Logic = "and", Nodes = [ParentCond(20, "eq", 9)] };
        var p = new DynamicParameters();

        var sql = (string)BuildWhere.Invoke(null, [group, p, fields, scope])!;

        sql.Should().Be(" AND (f_20 = (SELECT pv.v FROM (VALUES (1, @fv0)) pv(k, v) WHERE pv.k = c.f_10))");
        p.Get<string>("fv0").Should().Be("West");
    }

    [Fact]
    public void ReferenceComparedAsItsKey_ReadsThatKeyThroughTheReference()
    {
        // "Related D" (fid 12) stands in as a lookup (fid -1000012) of its parent's Department Name (table 9, fid 6).
        var standIn = new AppField
        {
            Id = -1000012, Fid = -1000012, Name = "Related D", TypeCode = "Lookup",
            Settings = """{"referenceFid":12,"sourceTableId":9,"sourceFid":6,"sourceTypeCode":"Text"}""",
        };
        var fields = new Dictionary<long, AppField>(Fields) { [-1000012] = standIn };
        var group = new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = -1000012, Operator = "eq", Value = "Sales" } }] };
        var p = new DynamicParameters();

        var sql = (string)BuildWhere.Invoke(null, [group, p, fields, Parent])!;

        sql.Should().Be(" AND ((SELECT lk.f_6 FROM data.t_9 lk WHERE lk.Id = c.f_12 AND lk.IsDeleted = 0) = @fv0)");
        p.Get<string>("fv0").Should().Be("Sales");
    }

    [Theory]
    [InlineData(false)]   // values never worked out
    [InlineData(true)]    // every parent's value is blank
    public void ParentField_Calculated_WithNoValueToCompareTo_MatchesNothing(bool allBlank)
    {
        var scope = allBlank
            ? Parent with { ComputedValues = new Dictionary<long, IReadOnlyDictionary<long, object?>> { [8] = new Dictionary<long, object?> { [1] = null, [2] = " " } } }
            : Parent;

        var (sql, _) = WhereWithParent(scope, ParentCond(7, "gt", 8));

        // Never dropped — that would count every child.
        sql.Should().Be(" AND (1 = 0)");
    }

    // ── Wildcard: a backslash before * or ? makes it the character itself ──

    [Theory]
    [InlineData("a*b", "a%b")]
    [InlineData("a?b", "a_b")]
    [InlineData(@"a\*b*", "a*b%")]
    [InlineData(@"a\?b", "a?b")]
    [InlineData("50%_[x]*", "50[%][_][[]x]%")]
    [InlineData(@"C:\dir*", @"c:\dir%")]
    public void Wildcard_TranslatesTheQuickbaseGrammarToLike(string pattern, string like)
    {
        var (sql, p) = Where(20, "wildcard", pattern);
        sql.Should().Contain("LIKE @fv0");
        p.Get<string>("fv0").Should().Be(like);
    }

    // ── Combined Text of a range: both ends, the end being a second column ──

    private static (string Value, string HasValue)? RangeValue(int targetFid, IReadOnlyDictionary<long, AppField> fields) =>
        ((string, string)?)typeof(RecordRepository).GetMethod("RangeValueExpr", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [targetFid, null, fields]);

    [Fact]
    public void RangeValue_OfARangeField_ReadsBothEndsAsJson()
    {
        var fields = new Dictionary<long, AppField> { [30] = new() { Id = 1030, Fid = 30, Name = "Term", TypeCode = "DateRange" } };

        RangeValue(30, fields).Should().Be((
            "(SELECT c.f_30 AS [start], c.f_30_e AS [end] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)",
            "(c.f_30 IS NOT NULL OR c.f_30_e IS NOT NULL)"));
    }

    [Fact]
    public void RangeValue_OfALookupOfARange_ReadsBothEndsFromTheParent()
    {
        var fields = new Dictionary<long, AppField>
        {
            [31] = new() { Id = 1031, Fid = 31, Name = "Client - Term", TypeCode = "Lookup",
                Settings = """{"referenceFid":10,"sourceTableId":5,"sourceFid":8,"sourceTypeCode":"NumericRange"}""" },
        };

        var (value, hasValue) = RangeValue(31, fields)!.Value;

        value.Should().Contain("SELECT lk.f_8 AS [start], lk.f_8_e AS [end]").And.Contain("lk.Id = c.f_10").And.EndWith("FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)");
        hasValue.Should().StartWith("EXISTS (SELECT 1 FROM").And.Contain("lk.f_8 IS NOT NULL OR lk.f_8_e IS NOT NULL");
    }

    [Fact]
    public void RangeValue_OfAnyOtherField_IsNone() =>
        RangeValue(20, Fields).Should().BeNull();
}
