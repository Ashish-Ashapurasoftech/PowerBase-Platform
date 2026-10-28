using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Binding;
using PowerBase.Formula.Types;

namespace PowerBase.UnitTests.Formulas;

public class FormulaCanonicalizerTests
{
    private static AppField Field(int fid, string name, string label, string typeCode = "Number") =>
        new() { Id = fid, Fid = fid, Name = name, Label = label, TypeCode = typeCode };

    private static FormulaCanonicalizer For(params AppField[] fields) => new(fields);

    private static readonly AppField Qty = Field(14, "C_qty", "Quantity");
    private static readonly AppField Price = Field(15, "C_price", "Unit Price");

    // ── typed → stored ──────────────────────────────────────────────────────

    [Fact]
    public void ToStored_turns_a_name_reference_into_an_id_reference_with_a_name_snapshot()
        => For(Qty, Price).ToStored("[Quantity] * [Unit Price]")
            .Should().Be("[fid_14|Quantity] * [fid_15|Unit Price]");

    [Fact]
    public void ToStored_leaves_an_id_reference_exactly_as_typed()
        => For(Qty).ToStored("[fid_14] + 1").Should().Be("[fid_14] + 1");

    [Fact]
    public void ToStored_matches_names_case_insensitively_and_by_internal_name()
        => For(Qty).ToStored("[quantity] + [C_QTY]").Should().Be("[fid_14|Quantity] + [fid_14|Quantity]");

    [Fact]
    public void ToStored_is_idempotent()
    {
        var once = For(Qty, Price).ToStored("[Quantity] * [Unit Price]");
        For(Qty, Price).ToStored(once).Should().Be(once);
    }

    [Fact]
    public void ToStored_leaves_unknown_names_and_table_aliases_alone()
        => For(Qty).ToStored("[Nope] + [_DBID_ORDERS] + [Quantity]")
            .Should().Be("[Nope] + [_DBID_ORDERS] + [fid_14|Quantity]");

    [Fact]
    public void ToStored_does_not_touch_text_literals_or_comments()
        => For(Qty).ToStored("\"[Quantity]\" // [Quantity]\n+ [Quantity]")
            .Should().Be("\"[Quantity]\" // [Quantity]\n+ [fid_14|Quantity]");

    [Fact]
    public void ToStored_preserves_spacing_around_the_reference()
        => For(Qty).ToStored("If( [ Quantity ] > 1 , 1 , 0 )").Should().Be("If( [fid_14|Quantity] > 1 , 1 , 0 )");

    [Fact]
    public void ToStored_leaves_a_malformed_expression_unchanged()
        => For(Qty).ToStored("[Quantity] + [Unclosed").Should().Be("[Quantity] + [Unclosed");

    [Fact]
    public void ToStored_does_not_convert_a_field_type_a_formula_cannot_read()
    {
        var link = Field(20, "C_doc", "Document", "ReportLink");
        For(link).ToStored("[Document]").Should().Be("[Document]");
    }

    [Fact]
    public void ToStored_converts_a_file_field_because_formulas_now_read_it_as_its_file_name()
    {
        var file = Field(20, "C_doc", "Document", "File");
        For(file).ToStored("[Document]").Should().Be("[fid_20|Document]");
    }

    [Fact]
    public void ToStored_escapes_brackets_in_a_label_so_the_result_still_parses()
    {
        var odd = Field(7, "C_odd", "A]B");
        var stored = For(odd).ToStored("[A\\]B] + 1");

        stored.Should().Be("[fid_7|A\\]B] + 1");
        new FormulaEngine().Compile(stored, new AppFieldSchema([odd]), FormulaType.Number).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ToStored_accepts_the_new_label_of_a_field_renamed_in_the_same_save()
    {
        var canonicalizer = new FormulaCanonicalizer([Qty], new Dictionary<string, long> { ["Count"] = 14 });

        canonicalizer.ToStored("[Count] + [Quantity]").Should().Be("[fid_14|Quantity] + [fid_14|Quantity]");
    }

    [Fact]
    public void An_extra_name_never_overrides_a_name_that_already_resolves()
    {
        var canonicalizer = new FormulaCanonicalizer([Qty, Price], new Dictionary<string, long> { ["Unit Price"] = 14 });

        canonicalizer.ToStored("[Unit Price]").Should().Be("[fid_15|Unit Price]");
    }

    // ── stored → displayed ──────────────────────────────────────────────────

    [Fact]
    public void ToDisplay_shows_the_current_label_so_a_rename_follows_through()
    {
        var stored = For(Qty).ToStored("[Quantity] * 2");
        var renamed = Field(14, "C_qty", "Item Count");

        For(renamed).ToDisplay(stored).Should().Be("[Item Count] * 2");
    }

    [Fact]
    public void ToDisplay_keeps_an_id_reference_as_an_id_reference()
        => For(Qty).ToDisplay("[fid_14] + [fid_14|Quantity]").Should().Be("[fid_14] + [Quantity]");

    [Fact]
    public void ToDisplay_falls_back_to_the_snapshot_name_for_a_deleted_field()
        => For(Price).ToDisplay("[fid_14|Quantity] + 1").Should().Be("[Quantity] + 1");

    [Fact]
    public void ToDisplay_leaves_plain_names_alone()
        => For(Qty).ToDisplay("[Quantity] + [Nope]").Should().Be("[Quantity] + [Nope]");

    [Fact]
    public void Typed_text_survives_a_store_then_display_round_trip()
    {
        var canonicalizer = For(Qty, Price);
        const string typed = "[Quantity] * [fid_15] + 1";

        canonicalizer.ToDisplay(canonicalizer.ToStored(typed)).Should().Be(typed);
    }

    // ── the whole point: a rename must not break the formula ───────────────

    [Fact]
    public void Stored_formula_still_compiles_and_binds_after_the_field_is_renamed()
    {
        var stored = For(Qty, Price).ToStored("[Quantity] * [Unit Price]");
        var afterRename = new[] { Field(14, "C_qty", "Item Count"), Price };

        var compiled = new FormulaEngine().Compile(stored, new AppFieldSchema(afterRename), FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().BeEquivalentTo(new long[] { 14, 15 });
    }

    [Fact]
    public void A_formula_left_in_the_old_name_form_does_break_after_a_rename()
    {
        var afterRename = new[] { Field(14, "C_qty", "Item Count") };

        new FormulaEngine().Compile("[Quantity]", new AppFieldSchema(afterRename), FormulaType.Number).HasErrors.Should().BeTrue();
    }

    [Fact]
    public void A_stored_reference_to_a_deleted_field_is_reported_as_unknown()
    {
        var stored = For(Qty).ToStored("[Quantity]");

        new FormulaEngine().Compile(stored, new AppFieldSchema([Price]), FormulaType.Number).HasErrors.Should().BeTrue();
    }

    // ── settings JSON ───────────────────────────────────────────────────────

    [Fact]
    public void ToStoredSettings_rewrites_only_the_expression_and_keeps_the_other_settings()
    {
        const string settings = """{"expression":"[Quantity]*2","decimalPlaces":2,"note":"[Quantity] stays"}""";

        var stored = For(Qty).ToStoredSettings("Formula_Number", settings)!;

        using var doc = JsonDocument.Parse(stored);
        doc.RootElement.GetProperty("expression").GetString().Should().Be("[fid_14|Quantity]*2");
        doc.RootElement.GetProperty("decimalPlaces").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("note").GetString().Should().Be("[Quantity] stays");
    }

    [Fact]
    public void Settings_conversion_matches_the_property_name_case_insensitively()
    {
        var stored = For(Qty).ToStoredSettings("Formula_Text", """{"Expression":"[Quantity]"}""")!;

        using var doc = JsonDocument.Parse(stored);
        doc.RootElement.GetProperty("Expression").GetString().Should().Be("[fid_14|Quantity]");
    }

    [Fact]
    public void Formula_Url_template_is_converted_too()
    {
        var stored = For(Qty).ToStoredSettings("Formula_Url", """{"template":"Concat(\"/q/\", [Quantity])"}""")!;

        using var doc = JsonDocument.Parse(stored);
        doc.RootElement.GetProperty("template").GetString().Should().Be("Concat(\"/q/\", [fid_14|Quantity])");
    }

    [Fact]
    public void Settings_round_trip_back_to_what_was_typed()
    {
        var canonicalizer = For(Qty);
        const string typed = """{"expression":"[Quantity] + 1","decimalPlaces":0}""";

        var shown = canonicalizer.ToDisplaySettings("Formula_Number", canonicalizer.ToStoredSettings("Formula_Number", typed))!;

        using var doc = JsonDocument.Parse(shown);
        doc.RootElement.GetProperty("expression").GetString().Should().Be("[Quantity] + 1");
    }

    [Fact]
    public void Non_ascii_text_is_not_escaped_when_settings_are_rewritten()
    {
        var stored = For(Qty).ToStoredSettings("Formula_Text", """{"expression":"Concat(\"₹ \", [Quantity])"}""")!;

        stored.Should().Contain("₹").And.NotContain("\\u20B9");
    }

    [Theory]
    [InlineData("Number", """{"expression":"[Quantity]"}""")]   // not a formula type
    [InlineData("Formula_Number", "{not json")]
    [InlineData("Formula_Number", """{"expression":"[Nope]"}""")] // nothing to convert
    [InlineData("Formula_Number", """{"decimalPlaces":2}""")]
    [InlineData("Formula_Number", "")]
    [InlineData("Formula_Number", null)]
    public void Settings_are_returned_untouched_when_there_is_nothing_to_convert(string typeCode, string? settings)
        => For(Qty).ToStoredSettings(typeCode, settings).Should().Be(settings);
}
