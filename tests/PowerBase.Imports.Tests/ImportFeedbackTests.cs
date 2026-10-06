using System.Globalization;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>The feedback file: every row that was not imported, with the reason, safe to open in a spreadsheet.</summary>
public class ImportFeedbackTests
{
    private static List<string[]> ParseCsv(string csv)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { fields.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add([.. fields]); fields.Clear(); }
            else cell.Append(c);
        }
        return rows;
    }

    [Fact]
    public async Task The_file_lists_each_rejected_row_with_its_reason_column_and_the_values_it_carried()
    {
        var h = ImportHarness.Create();
        h.Store.Existing["EXIST"] = 900;

        await h.RunAsync();

        var rows = ParseCsv(h.FeedbackCsv!);
        rows[0].Should().StartWith(["Source row", "Result", "Reason", "Column", "Details", "Existing record", "Name", "Qty"]);
        rows.Should().HaveCount(6); // header + the five bad rows
        var duplicate = rows.Single(r => r[0] == "9");
        duplicate[1].Should().Be("Error");
        duplicate[2].Should().Be("Already exists");
        duplicate[3].Should().Be("Name");
        duplicate[5].Should().Be("900", "the file points at the record that already holds the value");
        duplicate[6].Should().Be("EXIST", "and shows what the rejected row carried");
        rows.Single(r => r[0] == "13")[7].Should().Be("abc");
    }

    [Fact]
    public async Task Rows_left_out_by_a_rule_are_listed_as_skipped()
    {
        var h = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "x", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
            ColumnRules = [new() { DestFid = 9, RequireField = true }],
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 9, SourceFid = 9 }]
        }, new HarnessOptions
        {
            RowCount = 3, Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_9"] = id == 2 ? "" : "x" }
        });

        await h.RunAsync();

        var line = ParseCsv(h.FeedbackCsv!)[1];
        line.Take(4).Should().Equal("2", "Skipped", "Blank value (Require field)", "Note");
    }

    [Fact]
    public async Task A_run_that_rejects_nothing_has_no_feedback_file()
    {
        var h = ImportHarness.Create(options: new HarnessOptions
        {
            RowCount = 5, Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = "1", ["f_9"] = "x" }
        });

        await h.RunAsync();

        h.FeedbackCsv.Should().BeNull();
        h.Completion!.Value.FeedbackPath.Should().BeNull();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    [Fact]
    public async Task The_run_records_where_the_file_was_stored_but_the_page_only_needs_to_know_one_exists()
    {
        var h = ImportHarness.Create();

        await h.RunAsync();

        h.Completion!.Value.FeedbackPath.Should().Be("/files/feedback.csv");
    }

    [Fact]
    public async Task Text_a_spreadsheet_would_run_as_a_formula_is_neutralised()
    {
        var h = ImportHarness.Create(options: new HarnessOptions
        {
            RowCount = 4,
            Row = id => new Dictionary<string, object?>
            {
                ["Id"] = id, ["f_6"] = id switch { 1 => "=HYPERLINK(\"http://evil\")", 2 => "+cmd", 3 => "@SUM(A1)", _ => "-5" }, ["f_8"] = "oops", ["f_9"] = "x"
            }
        });

        await h.RunAsync(); // every row has a non-numeric Qty, so all four are rejected and listed

        var rows = ParseCsv(h.FeedbackCsv!);
        rows.Single(r => r[0] == "1")[6].Should().StartWith("'=");
        rows.Single(r => r[0] == "2")[6].Should().Be("'+cmd");
        rows.Single(r => r[0] == "3")[6].Should().Be("'@SUM(A1)");
        rows.Single(r => r[0] == "4")[6].Should().Be("-5", "a plain negative number is not a formula");
    }

    [Fact]
    public async Task Commas_quotes_and_line_breaks_in_values_stay_in_their_own_cell()
    {
        var h = ImportHarness.Create(options: new HarnessOptions
        {
            RowCount = 1,
            Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "a, \"b\"\nc", ["f_8"] = "oops", ["f_9"] = "x" }
        });

        await h.RunAsync();

        var rows = ParseCsv(h.FeedbackCsv!);
        rows.Should().HaveCount(2);
        rows[1][6].Should().Be("a, \"b\"\nc");
        rows[1].Should().HaveCount(rows[0].Length);
    }

    [Fact]
    public async Task A_storage_failure_does_not_fail_an_import_that_has_otherwise_finished()
    {
        var failing = ImportHarness.Create(options: new HarnessOptions { FailFeedbackStorage = true });
        failing.Store.Existing["EXIST"] = 900;
        await failing.RunAsync();

        failing.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
        failing.Completion!.Value.FeedbackPath.Should().BeNull();
        failing.Completion!.Value.Detail.Should().Contain("could not be saved");
        failing.Store.Inserted.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_file_is_written_as_the_run_goes_so_it_holds_every_rejected_row_not_only_those_kept_for_the_page()
    {
        // 6,000 rejected rows is more than the page keeps; the file must have all of them.
        var h = ImportHarness.Create(options: new HarnessOptions
        {
            RowCount = 6000, Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = "oops", ["f_9"] = "x" }
        });
        h.Runs.AddIssuesAsync(Arg.Any<long>(), Arg.Any<IReadOnlyList<ImportRunIssue>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await h.RunAsync();

        ParseCsv(h.FeedbackCsv!).Should().HaveCount(6001);
        h.Counters.Errored.Should().Be(6000);
    }

    [Fact]
    public void Source_values_are_written_in_a_stable_culture_independent_form()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            ImportFeedbackWriter.Format(12.5m).Should().Be("12.5");
            ImportFeedbackWriter.Format(new DateTime(2026, 3, 9, 14, 5, 0)).Should().Be("2026-03-09 14:05:00");
            ImportFeedbackWriter.Format(true).Should().Be("true");
            ImportFeedbackWriter.Format(null).Should().BeNull();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
