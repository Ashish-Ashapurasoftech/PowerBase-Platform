using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>Virtual columns: fixed text or formulas that exist only inside the import, calculated per row, usable as a mapping's source and
/// in other formulas, never created in any table.</summary>
public class ImportVirtualColumnTests
{
    private const int V1 = ImportVirtual.FidBase;
    private const int V2 = ImportVirtual.FidBase + 1;
    private const int V3 = ImportVirtual.FidBase + 2;

    private static ImportVirtualColumn Formula(int fid, string name, string formula) => new() { Fid = fid, Name = name, Kind = ImportVirtual.Formula, Formula = formula };
    private static ImportVirtualColumn Fixed(int fid, string name, string value) => new() { Fid = fid, Name = name, Kind = ImportVirtual.Fixed, StaticValue = value };

    private static ImportDefinitionConfig Config(ImportVirtualColumn[] virtuals, params ImportFieldMapping[] mappings) => new()
    {
        Name = "Virtual", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
        Mappings = [.. mappings], VirtualColumns = [.. virtuals]
    };

    private static ImportFieldMapping Field(int dest, int source) => new() { DestFid = dest, SourceFid = source };

    private static HarnessOptions Rows(int count = 4) => new()
    {
        RowCount = count,
        Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = id.ToString(), ["f_9"] = "note" + id }
    };

    [Fact]
    public async Task A_formula_virtual_column_can_be_the_source_of_a_field()
    {
        var h = ImportHarness.Create(Config([Formula(V1, "Label", "[Name] & \"-\" & [Note]")], Field(6, 6), Field(9, V1)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.Inserted.Select(r => r[9]).Should().Equal("n1-note1", "n2-note2", "n3-note3", "n4-note4");
    }

    [Fact]
    public async Task A_virtual_column_can_use_another_whatever_the_order_they_are_listed_in()
    {
        // Code uses Name, which is listed after it: Name is calculated first.
        var virtuals = new[] { Formula(V2, "Code", "[Name] & \"/\" & [Qty]"), Formula(V1, "Name2", "Upper([Note])") };
        virtuals[0].Formula = "[Name2] & \"/\" & [Qty]";

        var h = ImportHarness.Create(Config(virtuals, Field(6, 6), Field(9, V2)), Rows(2));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.Inserted.Select(r => r[9]).Should().Equal("NOTE1/1", "NOTE2/2");
    }

    [Fact]
    public async Task A_virtual_column_that_is_mapped_nowhere_writes_nothing_but_feeds_the_others()
    {
        var h = ImportHarness.Create(Config([Fixed(V1, "Tag", "tag"), Formula(V2, "Tagged", "[Tag] & [Name]")], Field(6, 6), Field(9, V2)), Rows(1));

        await h.RunAsync();

        h.Store.Inserted.Single()[9].Should().Be("tagn1");
        h.Store.Inserted.Single().Keys.Should().NotContain(k => k >= ImportVirtual.FidBase);
    }

    [Fact]
    public async Task A_fixed_virtual_column_mapped_to_a_number_field_is_converted_like_any_text_source()
    {
        var h = ImportHarness.Create(Config([Fixed(V1, "Five", "5")], Field(6, 6), Field(7, V1)), Rows(2));

        await h.RunAsync();

        h.Store.Inserted.Should().OnlyContain(r => Equals(r[7], 5m));
    }

    [Fact]
    public async Task A_formula_mapping_can_use_a_virtual_column_too()
    {
        var formula = new ImportFieldMapping { DestFid = 9, Source = ImportMappingSource.Formula, Formula = "[Tag] & \"?\"" };
        var h = ImportHarness.Create(Config([Fixed(V1, "Tag", "t")], Field(6, 6), formula), Rows(1));

        await h.RunAsync();

        h.Store.Inserted.Single()[9].Should().Be("t?");
    }

    [Fact]
    public async Task A_value_that_cannot_be_converted_is_reported_for_its_row_not_for_the_run()
    {
        var h = ImportHarness.Create(Config([Fixed(V1, "Word", "abc")], Field(6, 6), Field(7, V1)), Rows(3));

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().NotContain("virtual"); // not refused up front: each row is reported
        h.Store.Inserted.Should().BeEmpty();
        h.Issues.Should().HaveCount(3);
    }

    // ---- what is refused when the import is saved or run ----

    [Fact]
    public async Task Columns_that_use_each_other_are_refused_with_their_names()
    {
        var h = ImportHarness.Create(Config([Formula(V1, "A", "[B] & \"x\""), Formula(V2, "B", "[A] & \"y\"")], Field(6, 6), Field(9, V1)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("'A'").And.Contain("'B'").And.Contain("use each other");
    }

    [Fact]
    public async Task A_cycle_is_named_even_when_another_column_is_fine()
    {
        var h = ImportHarness.Create(Config([Formula(V1, "A", "[B]"), Formula(V2, "B", "[C]"), Formula(V3, "C", "[A]")], Field(6, 6), Field(9, V1)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("'A'").And.Contain("'B'").And.Contain("'C'");
    }

    [Fact]
    public async Task A_column_that_uses_one_that_does_not_exist_is_refused()
    {
        var h = ImportHarness.Create(Config([Formula(V1, "Broken", "[Nothing] & \"x\"")], Field(6, 6), Field(9, V1)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("'Broken'");
    }

    [Theory]
    [InlineData("Name")]   // a source column
    [InlineData("note")]   // case does not matter
    public async Task A_name_a_source_column_already_has_is_refused(string name)
    {
        var h = ImportHarness.Create(Config([Fixed(V1, name, "x")], Field(6, 6)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("already a column named");
    }

    [Fact]
    public async Task Two_virtual_columns_cannot_share_a_name()
    {
        var h = ImportHarness.Create(Config([Fixed(V1, "Tag", "a"), Fixed(V2, "TAG", "b")], Field(6, 6)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("already a column named");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a[b]")]
    public async Task A_name_must_be_present_and_free_of_brackets(string name)
    {
        var h = ImportHarness.Create(Config([Fixed(V1, name, "x")], Field(6, 6)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }

    [Fact]
    public async Task A_virtual_column_id_must_not_collide_with_a_real_column()
    {
        var h = ImportHarness.Create(Config([Fixed(8, "Tag", "x")], Field(6, 6)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("not valid");
    }

    [Fact]
    public async Task A_formula_virtual_column_without_a_formula_or_a_fixed_one_without_a_value_is_refused()
    {
        var noFormula = ImportHarness.Create(Config([new ImportVirtualColumn { Fid = V1, Name = "A", Kind = ImportVirtual.Formula }], Field(6, 6)), Rows());
        var noValue = ImportHarness.Create(Config([new ImportVirtualColumn { Fid = V1, Name = "B", Kind = ImportVirtual.Fixed }], Field(6, 6)), Rows());

        await noFormula.RunAsync();
        await noValue.RunAsync();

        noFormula.Completion!.Value.Detail.Should().Contain("Enter the formula");
        noValue.Completion!.Value.Detail.Should().Contain("Enter the fixed value");
    }

    [Fact]
    public async Task There_is_a_limit_on_how_many_virtual_columns_an_import_has()
    {
        var many = Enumerable.Range(0, ImportVirtual.MaxColumns + 1).Select(i => Fixed(ImportVirtual.FidBase + i, "C" + i, "x")).ToArray();
        var h = ImportHarness.Create(Config(many, Field(6, 6)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Detail.Should().Contain("at most");
    }

    [Fact]
    public async Task A_mapping_to_a_virtual_column_that_was_removed_is_refused()
    {
        var h = ImportHarness.Create(Config([], Field(6, 6), Field(9, V1)), Rows());

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }

    [Fact]
    public async Task An_import_without_virtual_columns_reads_the_source_directly()
    {
        var h = ImportHarness.Create(Config([], Field(6, 6), Field(9, 9)), Rows(3));

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[9]).Should().Equal("note1", "note2", "note3");
    }

    // ---- stored and read back ----

    [Fact]
    public void Virtual_columns_are_stored_with_the_import_and_read_back()
    {
        var def = new ImportDefinition { Id = 1, DestinationTableId = 11, ImportType = "copy" };
        var cfg = Config([Fixed(V1, "Tag", "t"), Formula(V2, "Both", "[Tag] & [Name]")], Field(6, 6));

        ImportConfigMapper.Apply(def, cfg);
        var back = ImportConfigMapper.ToConfig(def, ImportHarness.SourceTableId);

        back.VirtualColumns.Select(c => (c.Fid, c.Name, c.Kind, c.StaticValue, c.Formula))
            .Should().Equal((V1, "Tag", ImportVirtual.Fixed, "t", (string?)null), (V2, "Both", ImportVirtual.Formula, (string?)null, "[Tag] & [Name]"));
    }

    [Fact]
    public void An_import_saved_before_virtual_columns_existed_has_none()
    {
        var def = new ImportDefinition { Id = 1, DestinationTableId = 11, ImportType = "copy", OptionsJson = "{\"constraintPolicy\":\"abortIfAnyIssue\"}" };

        ImportConfigMapper.ToConfig(def, Guid.Empty).VirtualColumns.Should().BeEmpty();
    }

    [Fact]
    public void A_null_list_from_a_client_means_none()
    {
        var cfg = ImportJson.Deserialize<ImportDefinitionConfig>("{\"name\":\"x\",\"virtualColumns\":null}")!;

        cfg.VirtualColumns.Should().BeEmpty();
    }

    // ---- the Record ID# ----

    private static ImportDefinitionConfig RecordIdConfig() => new()
    {
        Name = "By id", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Merge, MergeKeyFid = 3,
        Mappings = [new() { DestFid = 3, SourceFid = 8 }, new() { DestFid = 9, SourceFid = 9 }]
    };

    [Fact]
    public async Task A_new_import_cannot_write_to_or_match_on_the_record_id()
    {
        var h = ImportHarness.Create(RecordIdConfig(), Rows());
        var plans = await h.PlanBuilder!.BuildAllAsync(RecordIdConfig(), ImportHarness.DestTableId, default);

        ImportRecordIdRule.Problem(plans, new HashSet<(Guid, int)>()).Should().Contain("assigned by the system");
    }

    [Fact]
    public async Task An_import_that_already_used_the_record_id_keeps_working()
    {
        var h = ImportHarness.Create(RecordIdConfig(), Rows());
        var plans = await h.PlanBuilder!.BuildAllAsync(RecordIdConfig(), ImportHarness.DestTableId, default);
        var legacy = ImportRecordIdRule.Used(RecordIdConfig(), ImportHarness.DestTableId);

        ImportRecordIdRule.Problem(plans, legacy).Should().BeNull();
    }

    [Fact]
    public async Task An_import_without_the_record_id_has_nothing_to_refuse()
    {
        var cfg = Config([], Field(6, 6), Field(9, 9));
        var h = ImportHarness.Create(cfg, Rows());
        var plans = await h.PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        ImportRecordIdRule.Problem(plans, new HashSet<(Guid, int)>()).Should().BeNull();
    }
}
