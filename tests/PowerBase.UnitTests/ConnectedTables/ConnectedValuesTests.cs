using FluentAssertions;
using PowerBase.Application.ConnectedTables.Mapping;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.ConnectedTables;

public class ConnectedValuesTests
{
    [Fact]
    public void Normalize_Numbers_IgnoreScale()
    {
        ConnectedValues.Normalize("Number", 12.5m).Should().Be(ConnectedValues.Normalize("Number", 12.5000m));
        ConnectedValues.Normalize("Number", "12.50").Should().Be("12.5");
        ConnectedValues.Normalize("Currency", 3L).Should().Be("3");
    }

    [Fact]
    public void Normalize_BlankText_EqualsNull()
    {
        ConnectedValues.Normalize("Text", "").Should().BeNull();
        ConnectedValues.Normalize("Text", null).Should().BeNull();
        ConnectedValues.Normalize("Text", DBNull.Value).Should().BeNull();
    }

    [Fact]
    public void Normalize_Date_DropsTime()
    {
        ConnectedValues.Normalize("Date", new DateTime(2026, 3, 1, 0, 0, 0)).Should().Be("2026-03-01");
        ConnectedValues.Normalize("Date", "2026-03-01").Should().Be("2026-03-01");
    }

    [Fact]
    public void Normalize_Boolean_AcceptsBitsAndText()
    {
        ConnectedValues.Normalize("Boolean", true).Should().Be("1");
        ConnectedValues.Normalize("Boolean", "false").Should().Be("0");
        ConnectedValues.Normalize("Boolean", 1).Should().Be("1");
    }

    [Fact]
    public void Normalize_Range_CombinesBothEnds()
    {
        var range = new Dictionary<string, object?> { ["start"] = 1m, ["end"] = 2.50m };
        ConnectedValues.Normalize("NumericRange", range).Should().Be("1|2.5");
    }

    [Fact]
    public void Read_BuiltInField_UsesItsColumn()
    {
        var recordId = new AppField { Fid = 3, IsSystem = true, PhysicalColumnName = "Id", TypeCode = "Number" };
        ConnectedValues.Read(recordId, new Dictionary<string, object?> { ["Id"] = 42L }, null).Should().Be(42L);
    }

    [Fact]
    public void Read_RangeField_CombinesItsTwoColumns()
    {
        var field = new AppField { Fid = 7, TypeCode = "DateRange" };
        var row = new Dictionary<string, object?> { ["f_7"] = new DateTime(2026, 1, 1), ["f_7_e"] = new DateTime(2026, 1, 5) };
        var value = ConnectedValues.Read(field, row, null);
        value.Should().BeOfType<Dictionary<string, object?>>().Which["end"].Should().Be(new DateTime(2026, 1, 5));
    }

    [Fact]
    public void Read_ComputedField_UsesProjectedValue()
    {
        var field = new AppField { Fid = 9, TypeCode = "Formula_Number" };
        ConnectedValues.Read(field, new Dictionary<string, object?>(), new Dictionary<long, object?> { [9] = 5m }).Should().Be(5m);
    }

    [Fact]
    public void Convert_UserToEmail_JoinsEmails()
    {
        var emails = new Dictionary<long, string?> { [11] = "a@x.test", [22] = "b@x.test" };
        ConnectedValues.Convert(ConnectedConversions.UserToEmail, "[11,22]", id => emails.GetValueOrDefault(id)).Should().Be("a@x.test; b@x.test");
        ConnectedValues.Convert(ConnectedConversions.UserToEmail, "99", _ => null).Should().BeNull();
    }

    [Fact]
    public void Hash_ChangesWithValueAndMappingVersion()
    {
        var a = ConnectedValues.Hash([(10, "x"), (11, null)], 1);
        ConnectedValues.Hash([(10, "x"), (11, null)], 1).Should().Equal(a);
        ConnectedValues.Hash([(10, "y"), (11, null)], 1).Should().NotEqual(a);
        ConnectedValues.Hash([(10, "x"), (11, null)], 2).Should().NotEqual(a);
        // A null and an empty-looking value never collide.
        ConnectedValues.Hash([(10, "x"), (11, "")], 1).Should().NotEqual(a);
    }

    [Fact]
    public void Key_NormalizesNumbersAndRejectsBlanks()
    {
        ConnectedValues.Key(42L).Should().Be("42");
        ConnectedValues.Key(42.0000m).Should().Be("42");
        ConnectedValues.Key("  ABC ").Should().Be("ABC");
        ConnectedValues.Key("   ").Should().BeNull();
        ConnectedValues.Key(new string('k', 600)).Should().StartWith("#").And.HaveLength(65);
    }
}
