using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Formulas;

/// <summary>The canonicalizer with app variables: <c>[Tax]</c> typed by name is stored by id, and shown by name.</summary>
public class FormulaCanonicalizerVariableTests
{
    private static AppField Field(int fid, string label) =>
        new() { Id = fid, Fid = fid, Name = $"C_{fid}", Label = label, TypeCode = "Number" };

    private static AppVariable Variable(long id, string name) => new() { Id = id, Name = name, Value = "1" };

    private static FormulaCanonicalizer For(AppField[] fields, params AppVariable[] variables) => new(fields, variables: variables);

    private static readonly AppField Quantity = Field(14, "Quantity");
    private static readonly AppField TaxField = Field(20, "Tax");

    // ── typed → stored ───────────────────────────────────────────────────────

    [Fact]
    public void A_variable_name_is_stored_by_id_with_a_name_snapshot()
        => For([Quantity], Variable(5, "Tax")).ToStored("ToNumber([Tax]) * [Quantity]")
            .Should().Be("ToNumber([var_5|Tax]) * [fid_14|Quantity]");

    [Fact]
    public void The_variable_wins_when_a_field_has_the_same_name()
        => For([Quantity, TaxField], Variable(5, "Tax")).ToStored("[Tax]").Should().Be("[var_5|Tax]");

    [Fact]
    public void A_field_is_stored_as_a_field_when_no_variable_has_its_name()
        => For([Quantity, TaxField], Variable(5, "Rate")).ToStored("[Tax]").Should().Be("[fid_20|Tax]");

    [Fact]
    public void Id_spellings_are_kept_as_typed()
        => For([TaxField], Variable(5, "Tax")).ToStored("[fid_20] + [var_5]").Should().Be("[fid_20] + [var_5]");

    [Fact]
    public void Storing_is_idempotent()
    {
        var canonicalizer = For([Quantity, TaxField], Variable(5, "Tax"));
        var once = canonicalizer.ToStored("[Tax] + [Quantity]");

        canonicalizer.ToStored(once).Should().Be(once);
    }

    [Fact]
    public void Without_variables_nothing_changes_for_fields()
        => new FormulaCanonicalizer([Quantity]).ToStored("[Quantity]").Should().Be("[fid_14|Quantity]");

    // ── stored → displayed ───────────────────────────────────────────────────

    [Fact]
    public void A_stored_variable_is_shown_by_its_current_name_so_a_rename_follows()
    {
        var stored = For([Quantity], Variable(5, "Tax")).ToStored("[Tax]");

        For([Quantity], Variable(5, "Levy")).ToDisplay(stored).Should().Be("[Levy]");
    }

    [Fact]
    public void A_deleted_variable_falls_back_to_the_name_it_was_saved_with()
        => For([Quantity]).ToDisplay("[var_5|Tax]").Should().Be("[Tax]");

    [Fact]
    public void A_variable_typed_by_id_stays_an_id()
        => For([Quantity], Variable(5, "Tax")).ToDisplay("[var_5]").Should().Be("[var_5]");

    [Fact]
    public void A_field_whose_name_is_also_a_variable_is_shown_by_its_id()
    {
        // [Tax] would now mean the variable, so showing the field as [Tax] would change it when re-saved.
        var canonicalizer = For([Quantity, TaxField], Variable(5, "Tax"));

        canonicalizer.ToDisplay("[fid_20|Tax] + [fid_14|Quantity]").Should().Be("[fid_20] + [Quantity]");
    }

    [Fact]
    public void Showing_then_saving_a_field_that_shares_a_variables_name_keeps_pointing_at_the_field()
    {
        var canonicalizer = For([Quantity, TaxField], Variable(5, "Tax"));
        const string stored = "[fid_20|Tax] * 2";

        var shown = canonicalizer.ToDisplay(stored);
        var resaved = canonicalizer.ToStored(shown);

        shown.Should().Be("[fid_20] * 2");
        resaved.Should().Be("[fid_20] * 2");
    }

    [Fact]
    public void A_field_keeps_its_name_when_no_variable_shares_it()
        => For([TaxField], Variable(5, "Rate")).ToDisplay("[fid_20|Tax]").Should().Be("[Tax]");

    [Fact]
    public void A_variable_and_a_field_each_round_trip_to_what_was_typed()
    {
        var canonicalizer = For([Quantity], Variable(5, "Tax"));
        const string typed = "ToNumber([Tax]) * [Quantity]";

        canonicalizer.ToDisplay(canonicalizer.ToStored(typed)).Should().Be(typed);
    }

    // ── inside a field's settings ────────────────────────────────────────────

    [Fact]
    public void The_conversion_applies_to_the_formula_text_inside_a_fields_settings()
    {
        var canonicalizer = For([Quantity], Variable(5, "Tax"));

        var stored = canonicalizer.ToStoredSettings("Formula_Number", """{"expression":"ToNumber([Tax])*2","decimalPlaces":2}""")!;
        var shown = canonicalizer.ToDisplaySettings("Formula_Number", stored)!;

        using var storedDoc = JsonDocument.Parse(stored);
        storedDoc.RootElement.GetProperty("expression").GetString().Should().Be("ToNumber([var_5|Tax])*2");
        storedDoc.RootElement.GetProperty("decimalPlaces").GetInt32().Should().Be(2);
        using var shownDoc = JsonDocument.Parse(shown);
        shownDoc.RootElement.GetProperty("expression").GetString().Should().Be("ToNumber([Tax])*2");
    }
}
