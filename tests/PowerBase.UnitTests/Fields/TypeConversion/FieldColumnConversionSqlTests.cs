using FluentAssertions;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Fields.TypeConversion;

public class FieldColumnConversionSqlTests
{
    [Fact]
    public void Names_BuiltFromFidOnly()
    {
        FieldColumnConversionSql.OldColumnName(14).Should().Be("f_14__conv_old");
        FieldColumnConversionSql.UniqueDigestColumnName(14).Should().Be("f_14_unique_hash");
    }

    [Fact]
    public void ToStorageText_UsesLanguageNeutralForms()
    {
        FieldColumnConversionSql.ToStorageText(null).Should().BeNull();
        FieldColumnConversionSql.ToStorageText("Mahesh").Should().Be("Mahesh");
        FieldColumnConversionSql.ToStorageText(1200.5m).Should().Be("1200.5");
        FieldColumnConversionSql.ToStorageText(true).Should().Be("1");
        FieldColumnConversionSql.ToStorageText(false).Should().Be("0");
        FieldColumnConversionSql.ToStorageText(4320L).Should().Be("4320");
        FieldColumnConversionSql.ToStorageText(new DateTime(2026, 10, 29, 13, 30, 5, 120)).Should().Be("2026-10-29T13:30:05.120");
        FieldColumnConversionSql.ToStorageText(new TimeSpan(13, 30, 0)).Should().Be("13:30:00");
    }

    [Fact]
    public void ToStorageText_DecimalIgnoresCurrentCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            FieldColumnConversionSql.ToStorageText(1.5m).Should().Be("1.5");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void BatchJson_SkipsNullsAndCarriesUserTextAsData()
    {
        var json = FieldColumnConversionSql.BatchJson(
        [
            new(1, "O'Brien\"; DROP TABLE x; --"),
            new(2, null),
            new(3, 12m),
        ]);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items[0].GetProperty("id").GetInt64().Should().Be(1);
        items[0].GetProperty("v").GetString().Should().Be("O'Brien\"; DROP TABLE x; --");
        items[1].GetProperty("v").GetString().Should().Be("12");
    }

    [Fact]
    public void BatchUpdateSql_ConvertsIntoTargetTypeFromJsonParameter()
    {
        var sql = FieldColumnConversionSql.BatchUpdateSql("data.t_7", "f_14", "DECIMAL(18,4)");

        sql.Should().Contain("UPDATE t SET f_14 = CONVERT(DECIMAL(18,4), m.v)");
        sql.Should().Contain("FROM data.t_7 t");
        sql.Should().Contain("OPENJSON(@json)");
    }
}
