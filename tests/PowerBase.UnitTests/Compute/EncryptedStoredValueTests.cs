using Microsoft.Extensions.Configuration;
using FluentAssertions;
using PowerBase.Application.Compute;
using PowerBase.Infrastructure.Compute;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>Values the worker works out in C# for encrypted apps (decrypted, then stored plain in c_), converted to the c_ column's type.</summary>
public class EncryptedStoredValueTests
{
    private static object? Stored(object? raw, string sqlType) =>
        MaterializedColumnWriter.ToStoredValue(raw, sqlType, MaterializedColumnWriter.ClrTypeOfSql(sqlType));

    [Fact]
    public void Text_is_cut_to_the_sort_key() =>
        Stored(new string('a', 450), "NVARCHAR(400)").Should().Be(new string('a', 400));

    [Fact]
    public void Empty_text_is_stored_as_null() => Stored("", "NVARCHAR(400)").Should().BeNull();

    [Fact]
    public void Counts_and_flags_keep_their_type()
    {
        Stored(3, "INT").Should().Be(3);
        Stored(true, "BIT").Should().Be(true);
        Stored("1", "BIT").Should().Be(true);
    }

    [Fact]
    public void Decimals_are_rounded_to_the_column_scale() =>
        Stored(1.23456789m, "DECIMAL(38,6)").Should().Be(1.234568m);

    [Fact]
    public void Dates_from_text_are_parsed() =>
        Stored("2026-11-23", "DATE").Should().Be(new DateTime(2026, 11, 23));

    [Fact]
    public void A_value_that_does_not_convert_is_stored_as_null() => Stored("not a number", "DECIMAL(38,4)").Should().BeNull();

    [Fact]
    public void Priority_tables_are_read_per_tenant_in_the_given_order()
    {
        var options = new ComputeOptions { PriorityTables = "17:243, 17:244,18:5, bad, 17:x" };
        options.PriorityTablesFor(17).Should().Equal(new Dictionary<long, int> { [243] = 0, [244] = 1 });
        options.PriorityTablesFor(18).Should().Equal(new Dictionary<long, int> { [5] = 0 });
        options.PriorityTablesFor(99).Should().BeEmpty();
        new ComputeOptions().PriorityTablesFor(17).Should().BeEmpty();
    }

    [Fact]
    public void The_worker_reads_parallel_build_and_priority_settings()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Compute:MaxSessionsPerDatabase"] = "6",
            ["Compute:BackgroundSessionsPerDatabase"] = "4",
            ["Compute:PriorityTables"] = "17:243,17:244",
        }).Build();
        var o = ComputeConfig.ReadCompute(config);
        o.MaxSessionsPerDatabase.Should().Be(6);
        o.BackgroundSessionsPerDatabase.Should().Be(4);
        o.PriorityTablesFor(17).Should().Equal(new Dictionary<long, int> { [243] = 0, [244] = 1 });
        ComputeConfig.ReadCompute(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).BackgroundSessionsPerDatabase.Should().Be(1);
    }

    [Fact]
    public void Lookup_and_summary_of_encrypted_data_go_through_the_value_table()
    {
        MaterializedColumnWriter.UsesValueTable(new LookupPlan(11, 30, 1, 6, 10, 8, "f_8", DecryptAppId: 2)).Should().BeTrue();
        MaterializedColumnWriter.UsesValueTable(new LookupPlan(11, 30, 1, 6, 10, 8, "f_8")).Should().BeFalse();
        MaterializedColumnWriter.UsesValueTable(new SummaryPlan(10, 30, 1, 11, 6, "Count", null, null, null, DecryptAppId: 1)).Should().BeTrue();
        MaterializedColumnWriter.UsesValueTable(new SummaryPlan(10, 30, 1, 11, 6, "Count", null, null, null)).Should().BeFalse();
    }
}
