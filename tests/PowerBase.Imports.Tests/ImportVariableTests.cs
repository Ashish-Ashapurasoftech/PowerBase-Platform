using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;

namespace PowerBase.Imports.Tests;

/// <summary>Import variables: named text values (always <c>var_</c>...) usable in any formula as <c>[var_name]</c> and in any filter value or
/// fixed value as <c>{{var_name}}</c>, for every table the import fills.</summary>
public class ImportVariableTests
{
    private static ImportVariable Var(string name, string value = "v") => new() { Name = name, Value = value };
    private static ImportVariable Calc(string name, string formula) => new() { Name = name, Kind = ImportVariables.Formula, Formula = formula };

    private static readonly EvaluationOptions Clock = new() { UtcNow = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc) };

    private static IReadOnlyDictionary<string, string> ValuesOf(IReadOnlyList<ImportVariable> variables) => ImportVariables.Evaluate(variables, new FormulaEngine(), Clock).Values;
    private static ImportDefinitionConfig Apply(ImportDefinitionConfig cfg) => ImportVariables.Apply(cfg, ValuesOf(cfg.Variables));

    private static FilterGroup Filter(string op, string value, long fid = 6) =>
        new() { Nodes = [new() { Condition = new() { FieldId = fid, Operator = op, Value = value } }] };

    private static ImportFieldMapping Static(int dest, string text) => new() { DestFid = dest, Source = ImportMappingSource.Static, StaticValue = text };
    private static ImportFieldMapping Formula(int dest, string formula) => new() { DestFid = dest, Source = ImportMappingSource.Formula, Formula = formula };

    // ---- the name rules ----

    [Theory]
    [InlineData("var_cutoff")]
    [InlineData("VAR_Cutoff2")]
    [InlineData("var_a_b_c")]
    public void A_prefixed_name_with_letters_digits_and_underscores_is_fine(string name) =>
        ImportVariables.Check([Var(name)]).Should().BeNull();

    [Theory]
    [InlineData("cutoff", "must start with var_")]
    [InlineData("vars_cutoff", "must start with var_")]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    [InlineData("var_", "at least one")]
    [InlineData("var_a b", "letters, numbers and underscores")]
    [InlineData("var_a-b", "letters, numbers and underscores")]
    [InlineData("var_[x]", "letters, numbers and underscores")]
    [InlineData("var_é", "letters, numbers and underscores")]
    [InlineData("var_K", "letters, numbers and underscores")]
    public void A_name_that_breaks_the_rules_is_refused_with_the_reason(string name, string reason) =>
        ImportVariables.Check([Var(name)]).Should().Contain(reason);

    [Fact]
    public void A_name_that_looks_like_an_id_reference_is_refused()
    {
        ImportVariables.Check([Var("var_5")]).Should().Contain("reserved");
        ImportVariables.Check([Var("var_123")]).Should().Contain("reserved");
        ImportVariables.Check([Var("var_5x")]).Should().BeNull("only var_ followed by digits alone is an id reference");
    }

    [Fact]
    public void Names_must_be_unique_ignoring_case()
    {
        ImportVariables.Check([Var("var_a"), Var("VAR_A")]).Should().Contain("unique");
        ImportVariables.Check([Var("var_a"), Var("var_b")]).Should().BeNull();
    }

    [Fact]
    public void The_count_and_sizes_are_capped()
    {
        ImportVariables.Check(Enumerable.Range(0, ImportVariables.MaxCount).Select(i => Var("var_" + i + "x")).ToList()).Should().BeNull();
        ImportVariables.Check(Enumerable.Range(0, ImportVariables.MaxCount + 1).Select(i => Var("var_" + i + "x")).ToList()).Should().Contain("at most");
        ImportVariables.Check([Var("var_" + new string('a', ImportVariables.MaxNameLength))]).Should().Contain("too long");
        ImportVariables.Check([Var("var_a", new string('x', ImportVariables.MaxValueLength))]).Should().BeNull();
        ImportVariables.Check([Var("var_a", new string('x', ImportVariables.MaxValueLength + 1))]).Should().Contain("too long");
    }

    [Fact]
    public void A_null_value_is_an_empty_text_and_nothing_to_check_is_fine()
    {
        new ImportVariable { Name = "var_a", Value = null! }.Value.Should().Be("");
        ImportVariables.Check([]).Should().BeNull();
    }

    [Fact]
    public void A_variable_may_not_share_a_name_with_a_column_or_a_virtual_column()
    {
        var fields = new[] { ImportHarness.Field(10, 6, "var_x", "Text") };
        ImportVariables.Collision([Var("var_x")], fields, []).Should().Contain("already a column");
        ImportVariables.Collision([Var("VAR_X")], fields, []).Should().Contain("already a column", "ignoring case");
        ImportVariables.Collision([Var("var_y")], fields, [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "var_y" }]).Should().Contain("already a column");
        ImportVariables.Collision([Var("var_z")], fields, []).Should().BeNull();
        ImportVariables.Collision([], fields, []).Should().BeNull();
    }

    // ---- substituting {{var_name}} ----

    private static ImportDefinitionConfig Cfg(Action<ImportDefinitionConfig> tweak)
    {
        var cfg = new ImportDefinitionConfig { Name = "V", ImportType = ImportTypes.Copy, Mappings = [new() { DestFid = 6, SourceFid = 6 }] };
        tweak(cfg);
        return cfg;
    }

    [Fact]
    public void An_import_with_nothing_to_replace_is_returned_as_it_is()
    {
        var cfg = Cfg(c => { c.Conditions = Filter("eq", "plain"); c.Variables = [Var("var_a")]; });

        Apply(cfg).Should().BeSameAs(cfg);
        Apply(Cfg(_ => { })).Should().NotBeNull();
    }

    [Fact]
    public void A_token_is_replaced_in_every_place_a_value_can_be_typed_and_the_original_is_not_touched()
    {
        var cfg = Cfg(c =>
        {
            c.Variables = [Var("var_a", "AAA")];
            c.Conditions = new FilterGroup
            {
                Nodes = [new() { Group = new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 6, Operator = "eq", Value = "{{var_a}}" } }] } }]
            };
            c.TableConditions = Filter("contains", "x-{{var_a}}-y");
            c.Mappings = [Static(9, "{{var_a}}")];
            c.VirtualColumns = [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "Fx", Kind = ImportVirtual.Fixed, StaticValue = "{{var_a}}!" }];
            c.AdditionalTargets =
            [
                new ImportTargetConfig
                {
                    Conditions = Filter("eq", "{{var_a}}"), Mappings = [Static(9, "t:{{var_a}}")],
                    SheetMappings = [new ImportSheetMapping { Sheet = "S", Mappings = [Static(9, "{{var_a}}")], Conditions = Filter("eq", "{{var_a}}") }]
                }
            ];
            c.SheetMappings = [new ImportSheetMapping { Sheet = "S", Mappings = [Static(9, "s:{{var_a}}")], VirtualColumns = [new ImportVirtualColumn { StaticValue = "{{var_a}}" }] }];
        });

        var applied = Apply(cfg);

        applied.Conditions!.Nodes[0].Group!.Nodes[0].Condition!.Value.Should().Be("AAA");
        applied.TableConditions!.Nodes[0].Condition!.Value.Should().Be("x-AAA-y");
        applied.Mappings[0].StaticValue.Should().Be("AAA");
        applied.VirtualColumns[0].StaticValue.Should().Be("AAA!");
        applied.AdditionalTargets[0].Conditions!.Nodes[0].Condition!.Value.Should().Be("AAA");
        applied.AdditionalTargets[0].Mappings[0].StaticValue.Should().Be("t:AAA");
        applied.AdditionalTargets[0].SheetMappings[0].Mappings[0].StaticValue.Should().Be("AAA");
        applied.AdditionalTargets[0].SheetMappings[0].Conditions!.Nodes[0].Condition!.Value.Should().Be("AAA");
        applied.SheetMappings[0].Mappings[0].StaticValue.Should().Be("s:AAA");
        applied.SheetMappings[0].VirtualColumns[0].StaticValue.Should().Be("AAA");
        cfg.Mappings[0].StaticValue.Should().Be("{{var_a}}", "the saved configuration is never changed");
        cfg.Conditions!.Nodes[0].Group!.Nodes[0].Condition!.Value.Should().Be("{{var_a}}");
    }

    [Theory]
    [InlineData("{{var_a}}", "AAA")]
    [InlineData("{{ var_a }}", "AAA")]
    [InlineData("{{VAR_A}}", "AAA")]
    [InlineData("{{var_a}}{{var_a}}", "AAAAAA")]
    [InlineData("a{{var_a}}b{{var_b}}c", "aAAAbBBBc")]
    [InlineData("{{foo}} stays", "{{foo}} stays")]
    [InlineData("{single} stays", "{single} stays")]
    [InlineData("no token", "no token")]
    public void A_token_is_replaced_wherever_it_is_in_the_text(string typed, string expected)
    {
        var cfg = Cfg(c => { c.Variables = [Var("var_a", "AAA"), Var("var_b", "BBB")]; c.Mappings = [Static(9, typed)]; });

        Apply(cfg).Mappings[0].StaticValue.Should().Be(expected);
    }

    [Fact]
    public void A_number_a_date_a_time_and_a_checkbox_value_chosen_as_a_variable_arrive_as_the_plain_value()
    {
        var cfg = Cfg(c =>
        {
            c.Variables = [Var("var_min", "3"), Var("var_day", "2026-01-31"), Var("var_at", "13:45:00"), Var("var_on", "true")];
            c.Conditions = new FilterGroup
            {
                Logic = "and",
                Nodes =
                [
                    new() { Condition = new() { FieldId = 8, Operator = "gte", Value = "{{var_min}}" } },
                    new() { Condition = new() { FieldId = 9, Operator = "lt", Value = "{{var_day}}" } },
                    new() { Condition = new() { FieldId = 10, Operator = "eq", Value = "{{var_at}}" } },
                    new() { Condition = new() { FieldId = 11, Operator = "eq", Value = "{{var_on}}" } }
                ]
            };
        });

        var values = Apply(cfg).Conditions!.Nodes.Select(n => n.Condition!.Value).ToArray();

        values.Should().Equal("3", "2026-01-31", "13:45:00", "true");
    }

    [Fact]
    public void A_value_is_never_searched_for_another_token()
    {
        var cfg = Cfg(c => { c.Variables = [Var("var_a", "{{var_b}}"), Var("var_b", "BBB")]; c.Mappings = [Static(9, "{{var_a}}")]; });

        Apply(cfg).Mappings[0].StaticValue.Should().Be("{{var_b}}");
    }

    [Fact]
    public void A_name_nobody_defined_is_an_error_not_kept_as_text()
    {
        var cfg = Cfg(c => { c.Variables = [Var("var_a")]; c.Mappings = [Static(9, "{{var_nope}}")]; });

        var act = () => Apply(cfg);

        act.Should().Throw<ValidationException>().WithMessage("*Unknown variable {{var_nope}}*");
        ImportVariables.TryApply(cfg, ValuesOf(cfg.Variables), out _).Should().Contain("var_nope");
    }

    // ---- end to end: a run uses the variables ----

    private static ImportDefinitionConfig RunConfig(Action<ImportDefinitionConfig> tweak)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Variables", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }]
        };
        tweak(cfg);
        return cfg;
    }

    private static HarnessOptions Source(int rows = 40, bool rowFilter = false) => new()
    {
        RowCount = rows, EncryptedSourceName = rowFilter,
        Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = id.ToString(), ["f_9"] = "note" + id }
    };

    [Fact]
    public async Task A_variable_in_a_filter_value_filters_like_the_text_typed_there()
    {
        var typed = ImportHarness.Create(RunConfig(c => c.Conditions = Filter("startsWith", "n2")), Source(rowFilter: true));
        await typed.RunAsync();

        var variable = ImportHarness.Create(RunConfig(c => { c.Variables = [Var("var_prefix", "n2")]; c.Conditions = Filter("startsWith", "{{var_prefix}}"); }), Source(rowFilter: true));
        await variable.RunAsync();

        typed.Store.Inserted.Should().HaveCount(11, "n2, n20..n29");
        variable.Store.Inserted.Select(r => r[6]).Should().Equal(typed.Store.Inserted.Select(r => r[6]));
        variable.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    [Fact]
    public async Task A_variable_as_a_fixed_value_is_written_to_every_row()
    {
        var h = ImportHarness.Create(RunConfig(c => { c.Variables = [Var("var_label", "Imported")]; c.Mappings.Add(Static(9, "{{var_label}}")); }), Source(5));

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(5).And.OnlyContain(r => Equals(r[9], "Imported"));
    }

    [Fact]
    public async Task A_variable_in_a_formula_mapping_is_a_text_constant()
    {
        var h = ImportHarness.Create(RunConfig(c => { c.Variables = [Var("var_sep", "|")]; c.Mappings.Add(Formula(9, "[Name] & [var_sep] & [Note]")); }), Source(3));

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[9]).Should().Equal("n1|note1", "n2|note2", "n3|note3");
    }

    [Fact]
    public async Task A_formula_can_turn_a_variable_into_a_number()
    {
        var h = ImportHarness.Create(RunConfig(c => { c.Variables = [Var("var_five", "5")]; c.Mappings.Add(Formula(7, "ToNumber([var_five])")); }), Source(2));

        await h.RunAsync();

        h.Store.Inserted.Should().OnlyContain(r => Equals(r[7], 5m));
    }

    [Fact]
    public async Task A_variable_in_a_virtual_column_formula_and_a_fixed_virtual_column_reaches_the_field()
    {
        var h = ImportHarness.Create(RunConfig(c =>
        {
            c.Variables = [Var("var_sep", "/"), Var("var_tag", "T")];
            c.VirtualColumns =
            [
                new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "Joined", Kind = ImportVirtual.Formula, Formula = "[Name] & [var_sep] & [Note]" },
                new ImportVirtualColumn { Fid = ImportVirtual.FidBase + 1, Name = "Tag", Kind = ImportVirtual.Fixed, StaticValue = "{{var_tag}}" }
            ];
            c.Mappings.Add(new ImportFieldMapping { DestFid = 9, SourceFid = ImportVirtual.FidBase });
        }), Source(2));

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[9]).Should().Equal("n1/note1", "n2/note2");
    }

    [Fact]
    public async Task Every_table_the_import_fills_sees_the_same_variable()
    {
        const long contactsId = 12;
        var contactsPublicId = Guid.NewGuid();
        var contacts = new HarnessDestination
        {
            Id = contactsId, PublicId = contactsPublicId, Name = "Contacts",
            Fields = [ImportHarness.RecordId(contactsId), ImportHarness.Field(contactsId, 6, "Title", "Text"), ImportHarness.Field(contactsId, 8, "Code", "Text", required: true, unique: true)]
        };
        var options = Source(6, rowFilter: true);
        options.ExtraDestinations = [contacts];
        var h = ImportHarness.Create(RunConfig(c =>
        {
            c.Variables = [Var("var_label", "Imported"), Var("var_pick", "n1")];
            c.Mappings.Add(Static(9, "{{var_label}}"));
            c.AdditionalTargets =
            [
                new ImportTargetConfig
                {
                    DestinationTableId = contactsPublicId, ImportType = ImportTypes.Copy,
                    Mappings = [Static(6, "{{var_label}}"), new() { DestFid = 8, SourceFid = 6 }],
                    Conditions = Filter("eq", "{{var_pick}}")
                }
            ];
        }), options);

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(6).And.OnlyContain(r => Equals(r[9], "Imported"));
        h.Store.InsertedByTable[contactsId].Should().ContainSingle().Which[6].Should().Be("Imported");
        h.Store.InsertedByTable[contactsId].Single()[8].Should().Be("n1", "the table's own filter used the variable too");
    }

    [Fact]
    public async Task Text_that_only_looks_like_a_token_is_written_as_typed_and_a_value_is_not_expanded_again()
    {
        var h = ImportHarness.Create(RunConfig(c =>
        {
            c.Variables = [Var("var_a", "{{var_b}}"), Var("var_b", "BBB")];
            c.Mappings.Add(Static(9, "{{foo}} {{var_a}}"));
        }), Source(1));

        await h.RunAsync();

        h.Store.Inserted.Single()[9].Should().Be("{{foo}} {{var_b}}");
    }

    [Fact]
    public async Task An_unknown_variable_fails_the_run_before_anything_is_written()
    {
        var h = ImportHarness.Create(RunConfig(c => c.Mappings.Add(Static(9, "{{var_missing}}"))), Source(3));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }

    [Theory]
    [InlineData("label")]
    [InlineData("var_5")]
    [InlineData("var_a b")]
    public async Task A_bad_variable_name_fails_the_run_before_anything_is_written(string name)
    {
        var h = ImportHarness.Create(RunConfig(c => c.Variables = [Var(name)]), Source(3));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }

    [Fact]
    public async Task A_variable_named_like_a_virtual_column_fails_the_run()
    {
        var h = ImportHarness.Create(RunConfig(c =>
        {
            c.Variables = [Var("var_x")];
            c.VirtualColumns = [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "var_x", Kind = ImportVirtual.Fixed, StaticValue = "1" }];
        }), Source(3));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }

    [Fact]
    public async Task A_formula_naming_a_variable_that_does_not_exist_fails_the_run()
    {
        var h = ImportHarness.Create(RunConfig(c => { c.Variables = [Var("var_a")]; c.Mappings.Add(Formula(9, "[var_b]")); }), Source(3));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }

    [Fact]
    public async Task An_import_without_variables_runs_exactly_as_before()
    {
        var h = ImportHarness.Create(RunConfig(c => c.Mappings.Add(Static(9, "{{not a variable}}"))), Source(3));

        await h.RunAsync();

        h.Store.Inserted.Should().OnlyContain(r => Equals(r[9], "{{not a variable}}"));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    // ---- formula variables: typed, worked out once ----

    [Fact]
    public void A_formula_variable_is_worked_out_once_and_each_type_has_the_text_a_filter_value_takes()
    {
        var (values, error) = ImportVariables.Evaluate(
        [
            Var("var_text", "hello"), Calc("var_n", "2 + 3"), Calc("var_dec", "10 / 4"), Calc("var_b", "1 = 1"), Calc("var_day", "Date(2026, 1, 31)"),
            Calc("var_today", "Today()"), Calc("var_back", "Today() - Days(30)"), Calc("var_label", "\"a\" & \"b\""), Calc("var_blank", "\"\"")
        ], new FormulaEngine(), Clock);
        error.Should().BeNull();

        values["var_text"].Should().Be("hello");
        values["var_n"].Should().Be("5");
        values["var_dec"].Should().Be("2.5");
        values["var_b"].Should().Be("true");
        values["var_day"].Should().Be("2026-01-31");
        values["var_today"].Should().Be("2026-03-15", "worked out with the run's clock");
        values["var_back"].Should().Be("2026-02-13");
        values["var_label"].Should().Be("ab");
        values["var_blank"].Should().Be("");
    }

    [Fact]
    public void A_variable_may_use_another_whatever_the_order_and_not_a_source_column()
    {
        ValuesOf([Calc("var_b", "[var_a] * 2"), Calc("var_a", "21")])["var_b"].Should().Be("42");

        var (_, error) = ImportVariables.Evaluate([Calc("var_x", "[Name] & \"!\"")], new FormulaEngine(), Clock);
        error.Should().Contain("var_x", "a variable is global: it has no row to read a column from");
    }

    [Fact]
    public void A_circular_unknown_or_wrongly_typed_formula_is_refused_naming_the_variable()
    {
        ImportVariables.Evaluate([Calc("var_a", "[var_b]"), Calc("var_b", "[var_a]")], new FormulaEngine(), Clock).Error.Should().Contain("variables").And.Contain("var_a");
        ImportVariables.Evaluate([Calc("var_a", "[var_nope] + 1")], new FormulaEngine(), Clock).Error.Should().Contain("var_a");
        ImportVariables.Evaluate([Calc("var_a", "1 +")], new FormulaEngine(), Clock).Error.Should().Contain("var_a");
    }

    [Fact]
    public void A_formula_variable_needs_a_formula_and_an_unknown_kind_is_refused()
    {
        ImportVariables.Check([new ImportVariable { Name = "var_a", Kind = ImportVariables.Formula }]).Should().Contain("Enter the formula");
        ImportVariables.Check([new ImportVariable { Name = "var_a", Kind = "other" }]).Should().Contain("fixed value or a formula");
        ImportVariables.Check([Calc("var_a", new string('1', ImportPlanBuilder.MaxFormulaLength + 1))]).Should().Contain("too long");
        ImportVariables.Check([Calc("var_a", "1")]).Should().BeNull();
    }

    [Fact]
    public void Variables_are_read_as_virtual_columns_in_order_and_keep_the_id_they_were_given()
    {
        var withIds = ImportVariables.AsColumns([new ImportVariable { Name = "var_a", Value = "x", Fid = ImportVariables.FidBase + 7 }, Calc("var_b", "1")]);
        withIds.Select(c => (c.Fid, c.Name, c.Kind)).Should().Equal(
            (ImportVariables.FidBase + 7, "var_a", ImportVirtual.Fixed), (ImportVariables.FidFallback + 1, "var_b", ImportVirtual.Formula));
        withIds[1].Formula.Should().Be("1");
        withIds.Should().OnlyContain(c => ImportVirtual.IsVirtualFid(c.Fid));
    }

    [Fact]
    public void Deleting_another_variable_never_moves_the_id_of_one_that_has_its_own()
    {
        var a = new ImportVariable { Name = "var_a", Value = "x", Fid = ImportVariables.FidBase };
        var b = new ImportVariable { Name = "var_b", Value = "y", Fid = ImportVariables.FidBase + 1 };

        ImportVariables.AsColumns([a, b]).Single(c => c.Name == "var_b").Fid.Should().Be(ImportVariables.FidBase + 1);
        ImportVariables.AsColumns([b]).Single(c => c.Name == "var_b").Fid.Should().Be(ImportVariables.FidBase + 1);
    }

    [Fact]
    public void An_id_outside_the_range_or_given_to_two_variables_is_refused()
    {
        ImportVariables.Check([new ImportVariable { Name = "var_a", Fid = 5 }]).Should().Contain("not valid");
        ImportVariables.Check([new ImportVariable { Name = "var_a", Fid = ImportVariables.FidFallback }]).Should().Contain("not valid");
        ImportVariables.Check([new ImportVariable { Name = "var_a", Fid = ImportVariables.FidBase }, new ImportVariable { Name = "var_b", Fid = ImportVariables.FidBase }]).Should().Contain("not valid");
        ImportVariables.Check([new ImportVariable { Name = "var_a", Fid = ImportVariables.FidBase }, new ImportVariable { Name = "var_b" }]).Should().BeNull();
    }

    [Fact]
    public async Task A_number_variable_is_a_number_in_a_formula_and_in_a_filter()
    {
        var h = ImportHarness.Create(RunConfig(c =>
        {
            c.Variables = [Calc("var_rate", "2 + 1")];
            c.Mappings.Add(Formula(7, "[var_rate] * 10"));        // typed: no ToNumber needed
            c.Conditions = Filter("startsWith", "n{{var_rate}}"); // n3, n30..n39
        }), Source(40, rowFilter: true));

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(11).And.OnlyContain(r => Equals(r[7], 30m));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    [Fact]
    public async Task A_date_variable_can_be_a_mappings_source_and_a_formulas_value()
    {
        var h = ImportHarness.Create(RunConfig(c =>
        {
            c.Variables = [Calc("var_day", "Date(2026, 1, 31)")];
            c.Mappings.Add(Formula(9, "ToText([var_day])"));
        }), Source(2));

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(2).And.OnlyContain(r => ((string)r[9]!).Contains("2026"));
    }

    [Fact]
    public async Task A_variable_mapped_as_a_source_gives_every_row_its_value()
    {
        var h = ImportHarness.Create(RunConfig(c =>
        {
            c.Variables =
            [
                new ImportVariable { Name = "var_label", Value = "Imported", Fid = ImportVariables.FidBase },
                new ImportVariable { Name = "var_amount", Kind = ImportVariables.Formula, Formula = "40 + 2", Fid = ImportVariables.FidBase + 1 }
            ];
            c.Mappings.Add(new ImportFieldMapping { DestFid = 9, SourceFid = ImportVariables.FidBase });
            c.Mappings.Add(new ImportFieldMapping { DestFid = 7, SourceFid = ImportVariables.FidBase + 1 });
        }), Source(3));

        await h.RunAsync();

        h.Store.Inserted.Should().OnlyContain(r => Equals(r[9], "Imported") && Equals(r[7], 42m));
    }

    [Fact]
    public async Task A_formula_variable_is_the_same_for_every_table_the_import_fills()
    {
        const long contactsId = 12;
        var contactsPublicId = Guid.NewGuid();
        var contacts = new HarnessDestination
        {
            Id = contactsId, PublicId = contactsPublicId, Name = "Contacts",
            Fields = [ImportHarness.RecordId(contactsId), ImportHarness.Field(contactsId, 6, "Title", "Text"), ImportHarness.Field(contactsId, 8, "Code", "Text", required: true, unique: true)]
        };
        var options = Source(4);
        options.ExtraDestinations = [contacts];
        var h = ImportHarness.Create(RunConfig(c =>
        {
            c.Variables = [Calc("var_tag", "\"T\" & ToText(1 + 1)")];
            c.Mappings.Add(Formula(9, "[var_tag]"));
            c.AdditionalTargets = [new ImportTargetConfig { DestinationTableId = contactsPublicId, ImportType = ImportTypes.Copy, Mappings = [Formula(6, "[var_tag]"), new() { DestFid = 8, SourceFid = 6 }] }];
        }), options);

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().OnlyContain(r => Equals(r[9], "T2"));
        h.Store.InsertedByTable[contactsId].Should().OnlyContain(r => Equals(r[6], "T2"));
    }

    [Fact]
    public async Task A_broken_variable_formula_fails_the_run_before_anything_is_written()
    {
        var h = ImportHarness.Create(RunConfig(c => { c.Variables = [Calc("var_a", "[Name]")]; c.Mappings.Add(Formula(9, "[var_a]")); }), Source(3));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }

    [Fact]
    public void A_formula_variable_round_trips_through_a_save_and_old_variables_without_a_kind_are_fixed()
    {
        var entity = new ImportDefinition();
        ImportConfigMapper.Apply(entity, RunConfig(c => c.Variables = [Calc("var_a", "1 + 1"), Var("var_b", "x")]));

        var loaded = ImportConfigMapper.ToConfig(entity, Guid.Empty).Variables;

        loaded.Select(v => (v.Name, v.Kind, v.Formula, v.Value)).Should().Equal(("var_a", "formula", "1 + 1", ""), ("var_b", "fixed", null, "x"));
        ImportJson.Deserialize<ImportVariable>("{\"name\":\"var_old\",\"value\":\"v\"}")!.Kind.Should().Be("fixed");
    }

    // ---- saving and loading ----

    [Fact]
    public void The_variables_survive_a_save_and_a_load_and_an_import_without_any_stores_nothing_extra()
    {
        var entity = new ImportDefinition();
        var cfg = RunConfig(c => c.Variables = [Var("var_a", "1"), Var("var_b", "two words")]);

        ImportConfigMapper.Apply(entity, cfg);
        var loaded = ImportConfigMapper.ToConfig(entity, Guid.Empty);

        loaded.Variables.Select(v => (v.Name, v.Value)).Should().Equal(("var_a", "1"), ("var_b", "two words"));

        var plain = new ImportDefinition();
        ImportConfigMapper.Apply(plain, RunConfig(_ => { }));
        plain.OptionsJson.Should().BeNull("an import with no variables is saved exactly as before");
        ImportConfigMapper.ToConfig(plain, Guid.Empty).Variables.Should().BeEmpty();
    }

    [Fact]
    public void A_configuration_sent_with_null_variables_means_none()
    {
        var cfg = ImportJson.Deserialize<ImportDefinitionConfig>("{\"name\":\"x\",\"variables\":null}")!;

        cfg.Variables.Should().BeEmpty();
    }

    [Fact]
    public void A_changed_value_changes_the_saved_configuration_so_only_changed_imports_read_everything_again()
    {
        var a = ImportJson.Serialize(RunConfig(c => c.Variables = [Var("var_a", "1")]));
        var b = ImportJson.Serialize(RunConfig(c => c.Variables = [Var("var_a", "2")]));

        a.Should().NotBe(b);
    }
}
