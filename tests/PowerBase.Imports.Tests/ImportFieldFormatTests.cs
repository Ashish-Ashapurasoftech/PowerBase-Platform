using System.Globalization;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>The format rules a field's own settings define, enforced by the import as a normal save enforces them.</summary>
public class ImportFieldFormatTests
{
    private static readonly Guid Source = ImportHarness.SourceTableId;

    private static AppField Text(string? settings) => new() { Fid = 9, Name = "Code", Label = "Code", TypeCode = "Text", Settings = settings };

    [Fact]
    public void A_text_over_its_maximum_length_or_off_its_pattern_is_refused_and_blanks_are_not_checked()
    {
        var format = ImportFieldFormat.For(Text("{\"validation\":{\"maxLength\":3,\"regex\":\"^[a-z]+$\"}}"))!;

        format.Check("abc").Should().BeNull();
        format.Check("abcd").Should().Contain("3 characters or fewer");
        format.Check("ab1").Should().Contain("invalid format");
        format.Check("").Should().BeNull();
        format.Check(null).Should().BeNull();
    }

    [Fact]
    public void A_numeric_pattern_is_checked_against_the_number_as_plain_text()
    {
        var number = new AppField { Fid = 7, Name = "Qty", TypeCode = "Number", Settings = "{\"validation\":{\"regex\":\"^\\\\d{1,3}$\"}}" };
        var format = ImportFieldFormat.For(number)!;

        format.Check(12m).Should().BeNull();
        format.Check(1234m).Should().Contain("invalid format");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"validation\":{}}")]
    [InlineData("not json")]
    [InlineData("{\"validation\":{\"regex\":\"([unclosed\"}}")] // a broken pattern is no constraint, as in a normal save
    public void Fields_without_usable_format_rules_have_none(string? settings) => ImportFieldFormat.For(Text(settings)).Should().BeNull();

    [Fact]
    public void A_pattern_that_takes_forever_is_cut_off_and_counts_as_a_mismatch_instead_of_hanging_the_run()
    {
        var format = ImportFieldFormat.For(Text("{\"validation\":{\"regex\":\"^(a+)+$\"}}"))!;

        format.Check(new string('a', 40) + "!").Should().Contain("invalid format");
    }

    private static string Settings(string json) => json;

    private static HarnessOptions WithCodeRules(string json, Func<long, IReadOnlyDictionary<string, object?>>? row = null) => new()
    {
        RowCount = 4,
        Row = row ?? (id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = "1", ["f_9"] = id == 3 ? "TOOLONG" : "ok" }),
        ConfigureDestination = d => d.Single(f => f.Fid == 9).Settings = Settings(json)
    };

    private static ImportDefinitionConfig Copy() => new()
    {
        Name = "Format", SourceTableId = Source, ImportType = ImportTypes.Copy,
        Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 9, SourceFid = 9 }]
    };

    [Fact]
    public async Task A_row_whose_value_breaks_the_fields_format_is_reported_and_the_others_import()
    {
        var h = ImportHarness.Create(Copy(), WithCodeRules("{\"validation\":{\"maxLength\":5}}"));

        await h.RunAsync();

        h.Issues.Should().ContainSingle().Which.Should().Match<ImportRunIssue>(i =>
            i.SourceRowRef == 3 && i.ReasonCode == ImportReason.FormatViolation && i.Outcome == ImportOutcome.Errored && i.ColumnFid == 9);
        h.Store.Inserted.Should().HaveCount(3);
        h.Accounted.Should().Be(4);
    }

    [Fact]
    public async Task A_formula_result_is_held_to_the_same_format_as_a_field_value()
    {
        var config = Copy();
        config.Mappings[1] = new() { DestFid = 9, Source = ImportMappingSource.Formula, Formula = "Upper([Name]) & \"-LONG\"" };
        var h = ImportHarness.Create(config, WithCodeRules("{\"validation\":{\"maxLength\":4}}",
            id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = "1", ["f_9"] = "x" }));

        await h.RunAsync();

        h.Issues.Should().HaveCount(4).And.OnlyContain(i => i.ReasonCode == ImportReason.FormatViolation);
    }

    [Fact]
    public async Task A_fixed_value_that_breaks_the_format_is_refused_once_when_the_import_is_saved()
    {
        var config = Copy();
        config.Mappings[1] = new() { DestFid = 9, Source = ImportMappingSource.Static, StaticValue = "far too long" };
        var h = ImportHarness.Create(config, WithCodeRules("{\"validation\":{\"maxLength\":5}}"));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("5 characters or fewer");
    }

    [Fact]
    public async Task A_blank_value_passes_because_the_required_rules_not_the_format_decide_about_blanks()
    {
        var h = ImportHarness.Create(Copy(), WithCodeRules("{\"validation\":{\"regex\":\"^[a-z]+$\"}}",
            id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = "1", ["f_9"] = id % 2 == 0 ? "" : "abc" }));

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        h.Store.Inserted.Should().HaveCount(4);
    }
}
