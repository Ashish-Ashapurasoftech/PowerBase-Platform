using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Application.Reports;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Query rules measured on a live Quickbase realm (app in Eastern time) for Date / Time, multi-select, phone and Date Modified.</summary>
public class QbWhereTypedRulesTests
{
    private static readonly ISet<int> Known = new HashSet<int> { 1, 2, 3, 6, 10, 15, 16, 21 };
    private static readonly TimeZoneInfo Eastern = QbTimeZone.Resolve("America/New_York")!;

    private static string? TypeOf(long fid) => fid switch
    {
        1 or 2 or 16 => "timestamp", 15 => "date", 10 => "multitext", 21 => "phone", _ => "text",
    };

    private static FilterGroup Parse(string where) => QbWhereParser.Parse(where, Known, TypeOf, Eastern);

    private static IEnumerable<FilterCondition> All(FilterGroup g) =>
        g.Nodes.SelectMany(n => n.Condition is { } c ? new[] { c } : All(n.Group!));

    [Fact]
    public void TimestampEqualsDate_IsThatDayInAppZone()
    {
        var conds = All(Parse("{16.EX.'03-15-2026'}")).ToList();
        conds.Should().HaveCount(2);
        conds[0].Operator.Should().Be("gte");
        conds[0].Value.Should().Be("2026-03-15T04:00:00Z");   // EDT midnight
        conds[1].Operator.Should().Be("lt");
        conds[1].Value.Should().Be("2026-03-16T04:00:00Z");
    }

    [Theory]
    [InlineData("{16.AF.'03-15-2026'}", "gte", "2026-03-16T04:00:00Z")]
    [InlineData("{16.OAF.'03-15-2026'}", "gte", "2026-03-15T04:00:00Z")]
    [InlineData("{16.BF.'03-15-2026'}", "lt", "2026-03-15T04:00:00Z")]
    [InlineData("{16.OBF.'03-15-2026'}", "lt", "2026-03-16T04:00:00Z")]
    [InlineData("{16.AF.'2026-01-15'}", "gte", "2026-01-16T05:00:00Z")]  // EST
    public void TimestampRange_UsesWholeDays(string where, string op, string value)
    {
        var c = All(Parse(where)).Single();
        c.Operator.Should().Be(op);
        c.Value.Should().Be(value);
    }

    [Fact]
    public void TimestampWithTimeOfDay_IsLeftAsUtc() =>
        All(Parse("{16.AF.'03-15-2026 10:30'}")).Single().Value.Should().Be("03-15-2026 10:30");

    [Fact]
    public void DateField_IsNotShifted() =>
        All(Parse("{15.AF.'03-15-2026'}")).Single().Value.Should().Be("03-15-2026");

    [Fact]
    public void MultiSelect_SetAndHasAll()
    {
        All(Parse("{10.EX.'Blue;Red'}")).Single().Value.Should().Be("Blue,Red");
        var has = Parse("{10.HAS.'Red;Blue'}");
        All(has).Select(c => (c.Operator, c.Value)).Should().Equal(("includes", "Red"), ("includes", "Blue"));
        has.Nodes.Single().Group!.Logic.Should().Be("AND");
    }

    [Fact]
    public void MultiSelect_NotHas_KeepsBlanks() =>
        All(Parse("{10.XHAS.'Red'}")).Select(c => c.Operator).Should().Contain("isEmpty");

    [Theory]
    [InlineData("{21.EX.'(555) 010-0'}", "5550100")]
    [InlineData("{21.SW.'(555)'}", "555")]
    [InlineData("{21.CT.'-01'}", "01")]
    public void Phone_ComparesDigits(string where, string digits) =>
        All(Parse(where)).First().Value.Should().Be(digits);

    [Fact]
    public void DateModified_FallsBackToDateCreated()
    {
        var conds = All(Parse("{2.BF.'01-01-2027'}")).ToList();
        conds.Select(c => (c.FieldId, c.Operator)).Should().Contain(new[] { (2L, "lt"), (2L, "isEmpty"), (1L, "lt") });
    }

    [Fact]
    public void NotInRange_Today_KeepsBlanks() =>
        All(Parse("{16.XIR.'today'}")).Select(c => c.Operator).Should().Contain("isEmpty");

    [Fact]
    public void PhoneDigits_StripsEverythingElse() => QbWhereParser.PhoneDigits("(555) 010-1001").Should().Be("5550101001");
}
