using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>Import variables: named text values (always <c>var_</c>...) usable in any formula as <c>[var_name]</c> and in any filter value or
/// fixed value as <c>{{var_name}}</c>, for every table the import fills.</summary>
public class ImportVariableTests
{
    private static ImportVariable Var(string name, string value = "v") => new() { Name = name, Value = value };

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

    // ---- the schema formulas compile against ----

    [Fact]
    public void The_schema_finds_a_variable_by_name_ignoring_case_and_nothing_else()
    {
        var schema = ImportVariables.Schema([Var("var_Rate", "7")])!;

        schema.TryResolve("var_rate", out var found).Should().BeTrue();
        found.Value.Should().Be("7");
        schema.TryResolve("var_other", out _).Should().BeFalse();
        schema.TryResolve("var_1", out _).Should().BeFalse();
        ImportVariables.Schema([]).Should().BeNull("an import without variables compiles exactly as before");
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

        ImportVariables.Apply(cfg).Should().BeSameAs(cfg);
        ImportVariables.Apply(Cfg(_ => { })).Should().NotBeNull();
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

        var applied = ImportVariables.Apply(cfg);

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

        ImportVariables.Apply(cfg).Mappings[0].StaticValue.Should().Be(expected);
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

        var values = ImportVariables.Apply(cfg).Conditions!.Nodes.Select(n => n.Condition!.Value).ToArray();

        values.Should().Equal("3", "2026-01-31", "13:45:00", "true");
    }

    [Fact]
    public void A_value_is_never_searched_for_another_token()
    {
        var cfg = Cfg(c => { c.Variables = [Var("var_a", "{{var_b}}"), Var("var_b", "BBB")]; c.Mappings = [Static(9, "{{var_a}}")]; });

        ImportVariables.Apply(cfg).Mappings[0].StaticValue.Should().Be("{{var_b}}");
    }

    [Fact]
    public void A_name_nobody_defined_is_an_error_not_kept_as_text()
    {
        var cfg = Cfg(c => { c.Variables = [Var("var_a")]; c.Mappings = [Static(9, "{{var_nope}}")]; });

        var act = () => ImportVariables.Apply(cfg);

        act.Should().Throw<ValidationException>().WithMessage("*Unknown variable {{var_nope}}*");
        ImportVariables.TryApply(cfg, out _).Should().Contain("var_nope");
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
