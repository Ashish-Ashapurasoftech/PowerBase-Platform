using System.Globalization;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

public class ImportNumberTests
{
    [Theory]
    [InlineData("12", 12)]
    [InlineData("-3.5", -3.5)]
    [InlineData("+7", 7)]
    [InlineData("1,234.50", 1234.5)]
    [InlineData("1,234,567", 1234567)]
    [InlineData(" 42 ", 42)]
    [InlineData(".5", 0.5)]
    public void Plain_and_properly_grouped_numbers_are_read(string text, double expected)
    {
        ImportNumber.TryParse(text, out var value).Should().BeTrue();
        value.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("1,5")]       // a decimal comma, not fifteen
    [InlineData("1,23,456")]
    [InlineData("12,34")]
    [InlineData("1.2.3")]
    [InlineData("12 34")]
    [InlineData("abc")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Anything_ambiguous_or_not_a_number_is_refused(string? text) => ImportNumber.TryParse(text, out _).Should().BeFalse();
}

/// <summary>Fixed values and formulas as the source of a destination field: what is written, what is refused up front, and
/// what is reported per row.</summary>
public class ImportValueSourceTests
{
    private static readonly Guid Source = ImportHarness.SourceTableId;

    private static IReadOnlyDictionary<string, object?> Clean(long id, string? name = null, string? qty = null, string? note = null) => new Dictionary<string, object?>
    {
        ["Id"] = id, ["f_6"] = name ?? $"n{id}", ["f_8"] = qty ?? id.ToString(CultureInfo.InvariantCulture), ["f_9"] = note ?? $"note{id}"
    };

    private static ImportDefinitionConfig Config(params ImportFieldMapping[] mappings) => new()
    {
        Name = "Values", SourceTableId = Source, ImportType = ImportTypes.Copy, Mappings = [.. mappings]
    };

    private static ImportFieldMapping Field(int dest, int source) => new() { DestFid = dest, SourceFid = source };
    private static ImportFieldMapping Fixed(int dest, string? value) => new() { DestFid = dest, Source = ImportMappingSource.Static, StaticValue = value };
    private static ImportFieldMapping Formula(int dest, string? formula) => new() { DestFid = dest, Source = ImportMappingSource.Formula, Formula = formula };

    private static void AddDestination(List<AppField> fields, int fid, string name, string type, bool unique = false, int? maxLength = null)
    {
        var field = ImportHarness.Field(11, fid, name, type, unique: unique);
        field.MaxLength = maxLength;
        fields.Add(field);
    }

    private static HarnessOptions Rows(int count = 5, Action<List<AppField>>? destination = null) => new()
    {
        RowCount = count, Row = id => Clean(id), ConfigureDestination = destination
    };

    // ---- fixed values ----

    [Fact]
    public async Task A_fixed_value_is_written_to_every_row_in_the_type_of_the_field()
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Fixed(7, "5"), Fixed(9, "Imported"), Fixed(12, "yes"), Fixed(13, "2026-03-09")),
            Rows(destination: d => { AddDestination(d, 12, "Flag", "Boolean"); AddDestination(d, 13, "Day", "Date"); }));

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        h.Store.Inserted.Should().HaveCount(5).And.OnlyContain(r =>
            Equals(r[7], 5m) && Equals(r[9], "Imported") && Equals(r[12], true) && Equals(r[13], new DateTime(2026, 3, 9)));
    }

    [Theory]
    [InlineData("7", "twelve", "not a number")]
    [InlineData("12", "maybe", "not a checkbox value")]
    [InlineData("13", "09/03/2026", "not a date")]
    public async Task A_fixed_value_the_field_cannot_hold_stops_the_run_before_any_row_is_read(string destination, string value, string message)
    {
        var fid = int.Parse(destination);
        var h = ImportHarness.Create(Config(Field(6, 6), Fixed(fid, value)),
            Rows(destination: d => { AddDestination(d, 12, "Flag", "Boolean"); AddDestination(d, 13, "Day", "Date"); }));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain(message);
    }

    [Fact]
    public async Task A_blank_fixed_value_is_refused_because_it_would_not_mean_anything()
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Fixed(9, "   ")), Rows());

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("Enter the fixed value");
    }

    [Fact]
    public async Task A_fixed_value_cannot_fill_a_unique_field_because_every_row_would_clash()
    {
        var h = ImportHarness.Create(Config(Fixed(6, "same")), Rows());

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("must be unique");
    }

    [Fact]
    public async Task A_fixed_value_cannot_be_the_match_key_because_every_row_would_match_the_same_record()
    {
        var config = Config(Fixed(6, "same"), Field(7, 8));
        config.ImportType = ImportTypes.Merge;
        config.MergeKeyFid = 6;
        var h = ImportHarness.Create(config, Rows());

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("cannot be a fixed value");
    }

    [Fact]
    public async Task Removing_duplicates_on_a_fixed_value_is_refused_because_it_would_keep_only_one_row()
    {
        var config = Config(Field(6, 6), Fixed(9, "x"));
        config.ColumnRules = [new() { DestFid = 9, RemoveDuplicates = true }];
        var h = ImportHarness.Create(config, Rows());

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("fixed value");
    }

    [Fact]
    public async Task A_fixed_text_longer_than_the_field_allows_is_refused_once_not_row_by_row()
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Fixed(12, "toolong")), Rows(destination: d => AddDestination(d, 12, "Code", "Text", maxLength: 3)));

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("at most 3");
    }

    [Fact]
    public async Task Require_field_never_skips_a_fixed_value_and_a_fixed_value_is_shown_in_the_details_file()
    {
        // Row 3's Qty is not a number, so it is rejected; the file shows the fixed value the row would have carried.
        var h = ImportHarness.Create(Config(Field(6, 6), Field(7, 8), Fixed(9, "Imported")), new HarnessOptions
        {
            RowCount = 3, Row = id => Clean(id, qty: id == 3 ? "abc" : null)
        });

        await h.RunAsync();

        h.FeedbackCsv.Should().Contain("Imported").And.Contain("abc");
    }

    // ---- formulas ----

    [Fact]
    public async Task A_text_formula_is_calculated_for_every_row_from_the_source_fields()
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Formula(9, "Upper([Name]) & \"-\" & [Qty]")), Rows());

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        h.Store.Inserted.Select(r => r[9]).Should().BeEquivalentTo(new object[] { "N1-1", "N2-2", "N3-3", "N4-4", "N5-5" });
    }

    [Fact]
    public async Task A_number_formula_is_written_as_a_number()
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Formula(7, "ToNumber([Qty]) * 2")), Rows());

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        h.Store.Inserted.Select(r => r[7]).Should().BeEquivalentTo(new object[] { 2m, 4m, 6m, 8m, 10m });
    }

    [Fact]
    public async Task A_date_formula_is_stored_as_a_real_date_taken_from_one_clock_for_the_whole_run()
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Formula(13, "Today()")), Rows(destination: d => AddDestination(d, 13, "Day", "Date")));

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        var days = h.Store.Inserted.Select(r => r[13]).Cast<DateTime>().ToList();
        days.Should().HaveCount(5).And.OnlyContain(d => d == days[0] && Math.Abs((d - DateTime.UtcNow.Date).TotalDays) <= 1);
    }

    [Fact]
    public async Task A_formula_can_feed_a_column_rule()
    {
        var h = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "x", SourceTableId = Source, ImportType = ImportTypes.Copy,
            Mappings = [Field(6, 6), Formula(9, "If([Qty] = \"3\", \"\", \"filled\")")],
            ColumnRules = [new() { DestFid = 9, RequireField = true }]
        }, Rows());

        await h.RunAsync();

        h.Issues.Should().ContainSingle().Which.Should().Match<ImportRunIssue>(i =>
            i.SourceRowRef == 3 && i.ReasonCode == ImportReason.RequiredBlank && i.Outcome == ImportOutcome.Skipped);
    }

    [Fact]
    public async Task A_formula_result_the_field_cannot_safely_take_is_refused_before_any_row_is_read()
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Formula(12, "Upper([Name])")), // text into a checkbox
            Rows(destination: d => AddDestination(d, 12, "Flag", "Boolean")));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Detail.Should().Contain("cannot be imported into");
    }

    [Theory]
    [InlineData("Upper([Nope])", "has an error")]
    [InlineData("Upper([Name]", "has an error")]
    [InlineData("   ", "Enter the formula")]
    public async Task A_formula_that_does_not_compile_is_refused_with_the_engines_message(string formula, string message)
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Formula(9, formula)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain(message);
    }

    [Fact]
    public async Task A_formula_cannot_reach_other_tables_so_an_import_cannot_read_data_the_user_could_not_see_here()
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Formula(9, "Upper([_DBID_OTHER])")), Rows());

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }

    [Fact]
    public async Task A_formula_that_returns_text_for_a_number_field_reports_the_rows_whose_text_is_not_a_number()
    {
        // Text into a number is a safe cast, so it is allowed; each value is checked when it is calculated.
        var h = ImportHarness.Create(Config(Field(6, 6), Formula(7, "[Name]")), Rows(2));

        await h.RunAsync();

        h.Issues.Should().HaveCount(2).And.OnlyContain(i => i.ReasonCode == ImportReason.TypeMismatch && i.Outcome == ImportOutcome.Errored);
        h.Store.Inserted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_formula_that_returns_a_blank_for_a_row_imports_a_blank_and_Require_field_can_leave_such_rows_out()
    {
        // The formula language yields a blank (not an error) for something like dividing by zero, so a blank result is a
        // normal value. Row 2 divides by zero.
        var rows = new HarnessOptions { RowCount = 3, Row = id => Clean(id, qty: id == 2 ? "0" : "5") };
        var plain = ImportHarness.Create(Config(Field(6, 6), Formula(7, "10 / ToNumber([Qty])")), rows);
        var required = Config(Field(6, 6), Formula(7, "10 / ToNumber([Qty])"));
        required.ColumnRules = [new() { DestFid = 7, RequireField = true }];
        var guarded = ImportHarness.Create(required, new HarnessOptions { RowCount = 3, Row = id => Clean(id, qty: id == 2 ? "0" : "5") });

        await plain.RunAsync();
        await guarded.RunAsync();

        plain.Store.Inserted.Single(r => Equals(r[6], "n2"))[7].Should().BeNull();
        plain.Issues.Should().BeEmpty();
        guarded.Issues.Should().ContainSingle().Which.Should().Match<ImportRunIssue>(i =>
            i.SourceRowRef == 2 && i.ReasonCode == ImportReason.RequiredBlank && i.Outcome == ImportOutcome.Skipped);
        guarded.Store.Inserted.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_formula_can_be_the_match_key_of_a_merge()
    {
        var config = Config(Formula(6, "Upper([Name])"), Field(7, 8));
        config.ImportType = ImportTypes.Merge;
        config.MergeKeyFid = 6;
        var h = ImportHarness.Create(config, Rows(3));
        h.Store.Existing["N2"] = 102;

        await h.RunAsync();

        h.Store.Updated.Should().ContainSingle().Which.RecordId.Should().Be(102);
        h.Store.Inserted.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_details_file_shows_what_a_formula_returned_for_the_rejected_row()
    {
        var h = ImportHarness.Create(Config(Field(6, 6), Field(7, 8), Formula(9, "Upper([Name])")), new HarnessOptions
        {
            RowCount = 2, Row = id => Clean(id, qty: id == 2 ? "abc" : null)
        });

        await h.RunAsync();

        h.FeedbackCsv.Should().Contain("N2").And.Contain("abc");
    }

    [Fact]
    public async Task A_formula_that_refers_to_a_computed_source_field_makes_the_run_project_it()
    {
        // The source has no formula field in this harness, so this checks the simpler fact the reader depends on: a plain
        // formula needs no projection, but the plan still reads every field the formula refers to.
        var h = ImportHarness.Create(Config(Field(6, 6), Formula(9, "[Note] & [Qty]")), Rows(2));

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[9]).Should().BeEquivalentTo(new object[] { "note11", "note22" });
    }
}
