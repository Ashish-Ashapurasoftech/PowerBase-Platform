using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;

namespace PowerBase.Imports.Tests;

/// <summary>An import into one table may have a table-level filter too: it is combined with the import-wide filter (both must hold), so the first table
/// has the same filter every added table has, without a second table being needed.</summary>
public class ImportSingleTableFilterTests
{
    private static FilterGroup Cond(int field, string op, string value) => new() { Nodes = [new() { Condition = new() { FieldId = field, Operator = op, Value = value } }] };

    private static ImportDefinitionConfig OneTable(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "One table", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 9, SourceFid = 9 }]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    // Name is encrypted so a condition on it is checked on each row, the way a file's conditions are.
    private static HarnessOptions Source(int rows = 40) => new()
    {
        RowCount = rows, EncryptedSourceName = true,
        Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = id.ToString(), ["f_9"] = "note" + id }
    };

    // ---- the folding itself ----

    [Fact]
    public void Nothing_to_fold_returns_the_same_object()
    {
        var plain = OneTable();
        var onlyImportWide = OneTable(c => c.Conditions = Cond(6, "startsWith", "n"));

        ImportSingleTableFilter.Fold(plain).Should().BeSameAs(plain);
        ImportSingleTableFilter.Fold(onlyImportWide).Should().BeSameAs(onlyImportWide);
    }

    [Fact]
    public void The_tables_filter_alone_becomes_the_import_s_filter_and_the_saved_configuration_is_untouched()
    {
        var cfg = OneTable(c => c.TableConditions = Cond(6, "startsWith", "n2"));

        var folded = ImportSingleTableFilter.Fold(cfg);

        folded.TableConditions.Should().BeNull();
        folded.Conditions!.Logic.Should().Be("and");
        folded.Conditions.Nodes.Should().ContainSingle().Which.Group!.Nodes[0].Condition!.Value.Should().Be("n2");
        cfg.TableConditions.Should().NotBeNull("what is saved keeps the table's own filter");
        cfg.Conditions.Should().BeNull();
    }

    [Fact]
    public void Both_filters_must_hold()
    {
        var folded = ImportSingleTableFilter.Fold(OneTable(c =>
        {
            c.Conditions = Cond(6, "startsWith", "n");
            c.TableConditions = Cond(6, "endsWith", "5");
        }));

        folded.Conditions!.Logic.Should().Be("and");
        folded.Conditions.Nodes.Select(n => n.Group!.Nodes[0].Condition!.Value).Should().Equal("n", "5");
    }

    [Fact]
    public void An_import_with_several_tables_or_sheets_keeps_each_filter_where_it_is()
    {
        var several = OneTable(c =>
        {
            c.TableConditions = Cond(6, "eq", "x");
            c.AdditionalTargets = [new ImportTargetConfig { DestinationTableId = Guid.NewGuid() }];
        });
        var sheets = OneTable(c =>
        {
            c.TableConditions = Cond(6, "eq", "x");
            c.SheetMappings = [new ImportSheetMapping { Sheet = "S" }];
        });

        ImportSingleTableFilter.Fold(several).Should().BeSameAs(several);
        ImportSingleTableFilter.Fold(sheets).Should().BeSameAs(sheets);
    }

    // ---- a run ----

    [Fact]
    public async Task A_one_table_import_with_a_table_filter_only_takes_the_rows_that_pass_it()
    {
        var h = ImportHarness.Create(OneTable(c => c.TableConditions = Cond(6, "startsWith", "n2")), Source());

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[6]).Should().Equal(new[] { "n2" }.Concat(Enumerable.Range(20, 10).Select(i => "n" + i)));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    [Fact]
    public async Task The_import_wide_filter_and_the_table_filter_narrow_together()
    {
        var h = ImportHarness.Create(OneTable(c =>
        {
            c.Conditions = Cond(6, "startsWith", "n2");
            c.TableConditions = Cond(6, "endsWith", "5");
        }), Source());

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[6]).Should().Equal("n25");
    }

    [Fact]
    public async Task A_table_filter_that_matches_nothing_imports_nothing_without_failing()
    {
        var h = ImportHarness.Create(OneTable(c => c.TableConditions = Cond(6, "eq", "no such name")), Source());

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().NotBe(ImportRunStatus.Failed);
    }

    [Fact]
    public async Task Saving_such_an_import_is_accepted_and_plans_one_table()
    {
        var cfg = OneTable(c => c.TableConditions = Cond(6, "startsWith", "n2"));
        var h = ImportHarness.Create(cfg, Source());

        var plans = await h.PlanBuilder.BuildAllAsync(cfg, ImportHarness.DestTableId, CancellationToken.None);

        plans.Should().ContainSingle();
    }
}
