using FluentAssertions;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Entities;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

public class QbFieldMapperTests
{
    private static QbFieldMapping MapOne(QbField qb, params AppField[] pb) =>
        QbFieldMapper.Map([qb], [.. SystemFields(), .. pb]).Single();

    [Fact]
    public void Fields_pair_by_number_and_compatible_type()
    {
        var m = MapOne(new(6, "Name", "text", null, []), Pb(6, "Text", "Name"));
        m.Action.Should().Be(QbFieldAction.Load);
        m.Reason.Should().BeNull();
    }

    [Fact]
    public void The_five_system_fields_go_to_the_fixed_columns()
    {
        var maps = QbFieldMapper.Map(QbSystemFields(), SystemFields());
        maps.Should().OnlyContain(m => m.Action == QbFieldAction.System);
    }

    [Fact]
    public void A_missing_field_blocks_the_table_rather_than_being_dropped()
    {
        var m = MapOne(new(6, "Name", "text", null, []));
        m.Action.Should().Be(QbFieldAction.Block);
        QbFieldMapper.IsLoadable([m]).Should().BeFalse();
    }

    [Fact]
    public void An_import_placeholder_blocks()
    {
        var pb = Pb(9, "Text", "Total");
        pb.Description = QbFieldMapper.PlaceholderMarker + " because the formula failed";
        MapOne(new(9, "Total", "numeric", null, []), pb).Action.Should().Be(QbFieldAction.Block);
    }

    [Fact]
    public void A_type_that_cannot_hold_the_value_blocks()
    {
        MapOne(new(7, "Qty", "numeric", null, []), Pb(7, "Date", "Qty")).Action.Should().Be(QbFieldAction.Block);
    }

    [Fact]
    public void Computed_fields_are_never_written()
    {
        MapOne(new(8, "Total", "numeric", "formula", []), Pb(8, "Formula_Number", "Total")).Action.Should().Be(QbFieldAction.Computed);
        MapOne(new(10, "Name", "text", "lookup", [])).Action.Should().Be(QbFieldAction.Computed);
    }

    [Fact]
    public void Stored_in_quickbase_but_computed_in_powerbase_blocks()
    {
        MapOne(new(8, "Total", "numeric", null, []), Pb(8, "Formula_Number", "Total")).Action.Should().Be(QbFieldAction.Block);
    }

    [Fact]
    public void File_fields_are_skipped_until_the_pilot()
    {
        MapOne(new(12, "Doc", "file", null, []), Pb(12, "File", "Doc")).Action.Should().Be(QbFieldAction.Skip);
        MapOne(new(12, "Doc", "file", null, [])).Action.Should().Be(QbFieldAction.Skip);
    }

    [Fact]
    public void Address_parts_feed_their_composite_field()
    {
        var maps = QbFieldMapper.Map(
        [
            new(20, "Address", "address", null, [21, 22]),
            new(21, "Address: Street 1", "text", null, [], 20),
            new(22, "Address: Postal Code", "text", null, [], 20),
        ], [.. SystemFields(), Pb(20, "Address", "Address")]);
        maps[0].Action.Should().Be(QbFieldAction.Load);
        maps[1].Should().Match<QbFieldMapping>(m => m.Action == QbFieldAction.AddressPart && m.AddressKey == "street1" && m.CompositeFid == 20);
        maps[2].AddressKey.Should().Be("zip");
    }

    [Fact]
    public void A_different_label_still_loads_but_says_so()
    {
        var m = MapOne(new(6, "Customer", "text", null, []), Pb(6, "Text", "Customer Name"));
        m.Action.Should().Be(QbFieldAction.Load);
        m.Reason.Should().Contain("Label differs");
    }
}

public class QbValueTransformerTests
{
    private static (QbTransformedRow Row, List<QbIssue> Issues) Transform(QbField qb, AppField pb, string json, TestData.Lookups? lookups = null,
        QbTransformOptions? options = null, int[]? byKey = null)
    {
        var maps = QbFieldMapper.Map([.. QbSystemFields(), qb], [.. SystemFields(), pb]);
        var t = new QbValueTransformer(maps, lookups ?? new TestData.Lookups(), options ?? new QbTransformOptions { FallbackUserId = 99 }, byKey);
        var issues = new List<QbIssue>();
        return (t.Transform(Row(7, (qb.Id, json)), issues), issues);
    }

    private static object? Value(QbField qb, AppField pb, string json, TestData.Lookups? lookups = null, int[]? byKey = null) =>
        Transform(qb, pb, json, lookups, byKey: byKey).Row.Values[pb.Fid!.Value];

    [Theory]
    [InlineData("text", "Text", "\"Zürich GmbH 東京\"", "Zürich GmbH 東京")]
    [InlineData("rich-text", "RichText", "\"<b>x</b>\"", "<b>x</b>")]
    [InlineData("email", "Email", "\"a@b.com\"", "a@b.com")]
    public void Text_is_kept_exactly(string qbType, string pbType, string json, string expected) =>
        Value(new(6, "F", qbType, null, []), Pb(6, pbType, "F"), json).Should().Be(expected);

    [Fact]
    public void Numbers_keep_every_decimal_and_sign() =>
        Value(new(6, "F", "numeric", null, []), Pb(6, "Number", "F"), "-1234.56789").Should().Be(-1234.56789m);

    [Fact]
    public void Percent_is_stored_as_a_whole_number() =>
        Value(new(6, "F", "percent", null, []), Pb(6, "Percent", "F"), "0.125").Should().Be(12.5m);

    [Fact]
    public void Duration_milliseconds_become_minutes() =>
        Value(new(6, "F", "duration", null, []), Pb(6, "Duration", "F"), "5400000").Should().Be(90);

    [Fact]
    public void Dates_and_times_parse_without_culture()
    {
        Value(new(6, "F", "date", null, []), Pb(6, "Date", "F"), "\"1900-01-01\"").Should().Be(new DateTime(1900, 1, 1));
        Value(new(6, "F", "timestamp", null, []), Pb(6, "DateTime", "F"), "\"2026-03-09T14:05:00Z\"")
            .Should().Be(new DateTime(2026, 3, 9, 14, 5, 0, DateTimeKind.Utc));
        Value(new(6, "F", "timeofday", null, []), Pb(6, "Time", "F"), "\"08:30:00\"").Should().Be(new TimeSpan(8, 30, 0));
    }

    [Fact]
    public void Multi_select_becomes_a_json_array_and_keeps_commas_inside_a_choice() =>
        Value(new(6, "F", "multitext", null, []), Pb(6, "MultiSelect", "F"), "[\"a, b\", \"c\"]").Should().Be("[\"a, b\",\"c\"]");

    [Fact]
    public void Blank_values_are_stored_as_null()
    {
        Value(new(6, "F", "text", null, []), Pb(6, "Text", "F"), "\"\"").Should().BeNull();
        Value(new(6, "F", "numeric", null, []), Pb(6, "Number", "F"), "null").Should().BeNull();
    }

    [Fact]
    public void Users_become_powerbase_ids_by_email()
    {
        var lookups = new TestData.Lookups(new() { ["ann@x.com"] = 41, ["bo@x.com"] = 42 });
        Value(new(6, "F", "user", null, []), Pb(6, "User", "F"), "{\"email\":\"ann@x.com\",\"id\":\"1.a\",\"name\":\"Ann\"}", lookups).Should().Be("41");
        Value(new(6, "F", "multiuser", null, []), Pb(6, "MultiUser", "F"), "[{\"email\":\"ann@x.com\"},{\"email\":\"bo@x.com\"}]", lookups).Should().Be("[41,42]");
    }

    [Fact]
    public void An_unknown_user_is_reported_once_per_field_in_a_chunk_not_once_per_record()
    {
        var qb = new QbField(6, "Owner", "user", null, []);
        var maps = QbFieldMapper.Map([.. QbSystemFields(), qb], [.. SystemFields(), Pb(6, "User", "Owner")]);
        var t = new QbValueTransformer(maps, new TestData.Lookups(), new QbTransformOptions { FallbackUserId = 99 });
        var issues = new List<QbIssue>();
        foreach (var id in new long[] { 7, 8, 9 })
            t.Transform(Row(id, (6, "{\"email\":\"ghost@x.com\"}"), (4, "{\"email\":\"ghost@x.com\"}")), issues).Values[6].Should().BeNull();
        t.Transform(Row(10, (6, "{\"email\":\"other@x.com\"}")), issues);

        issues.Where(i => i.Code == "QBM_USER_NOT_MAPPED").Select(i => (i.QbRecordId, i.Message.Contains("ghost@x.com")))
            .Should().Equal((7L, true), (10L, false));
        issues.Should().ContainSingle(i => i.Code == "QBM_SYSTEM_USER_NOT_MAPPED" && i.QbRecordId == 7);
    }

    [Theory]
    [InlineData("99.98999999999999", "99.99", true)]
    [InlineData("7.000000000000001", "7", true)]
    [InlineData("1234567.89", "1234567.89", true)]
    [InlineData("1.234", "1.23", false)]
    [InlineData("100", "101", false)]
    [InlineData("abc", "abc", false)]
    [InlineData(null, "0", false)]
    // The allowance never grows past 0.000001: a large id-sized number off by one is a real difference.
    [InlineData("13475851116", "13475851117", false)]
    [InlineData("13475851116.0000001", "13475851116", true)]
    public void Numbers_that_differ_only_by_float_noise_are_the_same_value(string? expected, string? actual, bool same) =>
        QbValueCanonical.SameNumber(expected, actual).Should().Be(same);

    [Theory]
    [InlineData("Number", true)]
    [InlineData("Currency", true)]
    [InlineData("Percent", true)]
    [InlineData("Reference", false)]
    [InlineData("User", false)]
    [InlineData("Text", false)]
    [InlineData("Phone", false)]
    public void Only_quantity_fields_get_the_float_noise_allowance(string pbType, bool allowed) =>
        QbValueCanonical.QuantityTypes.Contains(pbType).Should().Be(allowed);

    [Fact]
    public void An_unknown_user_is_stored_empty_and_reported()
    {
        var (row, issues) = Transform(new(6, "Owner", "user", null, []), Pb(6, "User", "Owner"), "{\"email\":\"ghost@x.com\"}");
        row.Values[6].Should().BeNull();
        issues.Should().ContainSingle(i => i.Code == "QBM_USER_NOT_MAPPED" && i.QbRecordId == 7 && i.Message.Contains("ghost@x.com"));
    }

    [Fact]
    public void A_record_id_reference_is_kept_as_is_and_a_key_reference_is_translated()
    {
        Value(new(6, "Customer", "numeric", null, []), Pb(6, "Reference", "Customer"), "12").Should().Be(12L);
        var lookups = new TestData.Lookups(refs: new() { [(6, "EU")] = 3 });
        Value(new(6, "Region", "text", null, []), Pb(6, "Reference", "Region"), "\"EU\"", lookups, byKey: [6]).Should().Be(3L);
    }

    [Fact]
    public void A_record_id_reference_sent_as_a_whole_decimal_is_kept_and_a_fraction_is_rejected()
    {
        // Seen on a real app: QuickBase returned the Related File reference as 1.0.
        Value(new(6, "Related File", "numeric", null, []), Pb(6, "Reference", "Related File"), "1.0").Should().Be(1L);
        var (row, issues) = Transform(new(6, "Related File", "numeric", null, []), Pb(6, "Reference", "Related File"), "1.5");
        row.Values[6].Should().BeNull();
        issues.Should().ContainSingle(i => i.Code == "QBM_VALUE_NOT_CONVERTED");
    }

    [Fact]
    public void A_value_that_cannot_convert_is_stored_empty_and_reported_without_stopping_the_row()
    {
        var (row, issues) = Transform(new(6, "Qty", "numeric", null, []), Pb(6, "Number", "Qty"), "\"twelve\"");
        row.Values[6].Should().BeNull();
        issues.Should().ContainSingle(i => i.Code == "QBM_VALUE_NOT_CONVERTED");
    }

    [Fact]
    public void Address_parts_are_combined_into_the_powerbase_json()
    {
        List<QbField> qb = [.. QbSystemFields(), new(20, "Address", "address", null, [21, 23]), new(21, "Address: Street 1", "text", null, [], 20), new(23, "Address: City", "text", null, [], 20)];
        var t = new QbValueTransformer(QbFieldMapper.Map(qb, [.. SystemFields(), Pb(20, "Address", "Address")]), new TestData.Lookups(), new QbTransformOptions());
        var row = t.Transform(Row(1, (20, "\"10 Main St, Cambridge\""), (21, "\"10 Main St\""), (23, "\"Cambridge\"")), []);
        row.Values[20].Should().Be("{\"street1\":\"10 Main St\",\"city\":\"Cambridge\"}");
    }

    [Fact]
    public void System_fields_fill_the_fixed_columns_and_an_unknown_owner_falls_back_to_the_migration_user()
    {
        var t = new QbValueTransformer(QbFieldMapper.Map(QbSystemFields(), SystemFields()), new TestData.Lookups(new() { ["ann@x.com"] = 41 }),
            new QbTransformOptions { FallbackUserId = 99 });
        var issues = new List<QbIssue>();
        var row = t.Transform(Row(5, (1, "\"2020-01-02T03:04:05Z\""), (2, "\"2021-01-02T03:04:05Z\""),
            (4, "{\"email\":\"gone@x.com\"}"), (5, "{\"email\":\"ann@x.com\"}")), issues);
        row.CreatedOn.Should().Be(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        row.ModifiedOn.Should().Be(new DateTime(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        row.CreatedBy.Should().Be(99);
        row.ModifiedBy.Should().Be(41);
        issues.Should().ContainSingle(i => i.Code == "QBM_SYSTEM_USER_NOT_MAPPED");
    }

    [Fact]
    public void A_table_with_blocked_fields_cannot_be_transformed()
    {
        var maps = QbFieldMapper.Map([new(6, "X", "text", null, [])], []);
        var act = () => new QbValueTransformer(maps, new TestData.Lookups(), new QbTransformOptions());
        act.Should().Throw<InvalidOperationException>();
    }
}
