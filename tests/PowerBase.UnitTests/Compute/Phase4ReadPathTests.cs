using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using PowerBase.Application.Compute;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.ExportReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Repositories;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>Phase 4: reads use a Ready stored column for sort / filter / count / totals / export; the shown value stays dynamic.</summary>
public class Phase4ReadPathTests
{
    private const long Dept = 10, Emp = 11;

    private static AppField Summary(int fid, string function, string? resultType = null, string? targetType = "Currency", long id = 0) => new()
    {
        Id = id == 0 ? 10_000 + fid : id, AppTableId = Dept, Fid = fid, TypeCode = "Summary", Name = $"s{fid}",
        Settings = System.Text.Json.JsonSerializer.Serialize(new { ChildTableId = Emp, ReferenceFid = 6, Function = function,
            TargetFid = function is "Count" or "Exists" ? (int?)null : 7, TargetTypeCode = targetType, ResultTypeCode = resultType })
    };

    private static AppField Lookup(int fid, string? resultType = null, string? sourceType = "Text") => new()
    {
        Id = 20_000 + fid, AppTableId = Emp, Fid = fid, TypeCode = "Lookup", Name = $"l{fid}",
        Settings = System.Text.Json.JsonSerializer.Serialize(new { SourceTableId = Dept, ReferenceFid = 6, SourceFid = 6, SourceTypeCode = sourceType, ResultTypeCode = resultType })
    };

    private static AppField Plain(long table, int fid, string type) => new() { Id = table * 100 + fid, AppTableId = table, Fid = fid, TypeCode = type, Name = $"p{fid}" };

    [Fact]
    public void Only_ready_fields_are_replaced_and_the_originals_are_untouched()
    {
        var total = Summary(8, "Sum");
        var count = Summary(9, "Count");
        var name = Plain(Dept, 6, "Text");
        var fields = new List<AppField> { name, total, count };

        var sql = MaterializedReadFields.Substitute(fields, new HashSet<long> { total.Id });

        sql[0].Should().BeSameAs(name);
        sql[2].Should().BeSameAs(count, "not Ready: keeps today's path");
        sql[1].Should().NotBeSameAs(total);
        sql[1].PhysicalColumnName.Should().Be("c_8");
        sql[1].IsSystem.Should().BeTrue();
        sql[1].TypeCode.Should().Be("Currency");
        total.TypeCode.Should().Be("Summary", "the projector still gets the original field");
        total.PhysicalColumnName.Should().BeNull();
    }

    [Fact]
    public void Nothing_ready_returns_the_same_list() =>
        MaterializedReadFields.Substitute(new List<AppField> { Summary(8, "Sum") }, new HashSet<long>()).Should().HaveCount(1).And.ContainItemsAssignableTo<AppField>();

    [Theory]
    [InlineData("Count", null, "Number")]
    [InlineData("DistinctCount", null, "Number")]
    [InlineData("Exists", null, "Boolean")]
    [InlineData("Sum", null, "Currency")]
    [InlineData("Max", null, "Currency")]
    [InlineData("Avg", "Number", "Number")]   // ResultTypeCode wins when saved
    public void Summary_value_types(string function, string? resultType, string expected) =>
        MaterializedReadFields.ValueTypeOf(Summary(8, function, resultType)).Should().Be(expected);

    [Fact]
    public void Lookup_value_type_prefers_the_saved_result_type()
    {
        MaterializedReadFields.ValueTypeOf(Lookup(20)).Should().Be("Text");
        MaterializedReadFields.ValueTypeOf(Lookup(20, resultType: "Date", sourceType: "DateTime")).Should().Be("Date");
    }

    [Fact]
    public void A_field_with_no_known_value_type_is_not_read_from_its_column()
    {
        var unknown = Summary(8, "Min", targetType: null);
        MaterializedReadFields.ReadyFids([unknown], new HashSet<long> { unknown.Id }).Should().BeEmpty();
        MaterializedReadFields.Substitute([unknown], new HashSet<long> { unknown.Id })[0].Should().BeSameAs(unknown);
    }

    [Fact]
    public void Pending_work_is_checked_on_the_tables_the_value_comes_from()
    {
        MaterializedReadFields.InputTables(Summary(8, "Sum")).Should().BeEquivalentTo([Dept, Emp]);
        MaterializedReadFields.InputTables(Lookup(20)).Should().BeEquivalentTo([Emp, Dept]);
    }

    [Fact]
    public void Sorting_by_a_ready_summary_orders_by_its_stored_column_in_sql()
    {
        var total = Summary(8, "Sum");
        var lookup = new Dictionary<long, AppField> { [8] = MaterializedReadFields.StandIn(total)! };
        RecordRepository.BuildReportOrderBy([new SortSpec { FieldId = 8, Desc = true }], lookup, null).Should().Be("c_8 DESC");
    }

    [Fact]
    public void Sorting_by_a_summary_that_is_not_ready_is_left_to_memory_as_today()
    {
        var lookup = new Dictionary<long, AppField> { [8] = Summary(8, "Sum") };
        RecordRepository.BuildReportOrderBy([new SortSpec { FieldId = 8, Desc = true }], lookup, null).Should().Be("Id");
    }

    [Fact]
    public void Filtering_by_a_ready_summary_compares_its_stored_column_with_a_parameter()
    {
        var lookup = new Dictionary<long, AppField> { [8] = MaterializedReadFields.StandIn(Summary(8, "Sum"))! };
        var p = new DynamicParameters();
        var where = RecordRepository.BuildFilterTreeWhere(new FilterGroup
        {
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 8, Operator = "gt", Value = "75000" } }]
        }, p, lookup);
        where.Should().Contain("c_8").And.NotContain("75000");
        p.ParameterNames.Should().NotBeEmpty();
    }

    [Fact]
    public void A_ready_field_is_read_from_its_stored_column_never_from_f()
    {
        var cols = RecordRepository.BuildFieldColumnList([Plain(Dept, 6, "Text"), MaterializedReadFields.StandIn(Summary(8, "Sum"))!]);
        cols.Should().Be(", c_8, f_6");   // global search re-checks a match against c_8; f_8 does not exist
    }

    // ── 12B: a definition change stops the columns built from it ──────────────────────────────────────

    [Fact]
    public void Changing_a_lookups_source_or_a_summarys_target_invalidates_them_and_nothing_else()
    {
        var lookup = Lookup(20);
        var total = Summary(8, "Sum");
        var count = Summary(9, "Count");
        var candidates = new List<AppField> { lookup, total, count };

        MaterializedFieldStateRepository.AffectedBy(candidates, new HashSet<FieldKey> { new(Dept, 6) }, new HashSet<long>())
            .Should().BeEquivalentTo([lookup.Id], "the lookup pulls Dept.f_6");
        MaterializedFieldStateRepository.AffectedBy(candidates, new HashSet<FieldKey> { new(Emp, 7) }, new HashSet<long>())
            .Should().BeEquivalentTo([total.Id], "Sum reads Emp.f_7; Count does not");
        MaterializedFieldStateRepository.AffectedBy(candidates, new HashSet<FieldKey> { new(Emp, 6) }, new HashSet<long>())
            .Should().BeEquivalentTo([lookup.Id, total.Id, count.Id], "all three go through the reference");
        MaterializedFieldStateRepository.AffectedBy(candidates, new HashSet<FieldKey> { new(Emp, 99) }, new HashSet<long>())
            .Should().BeEmpty();
        MaterializedFieldStateRepository.AffectedBy(candidates, new HashSet<FieldKey>(), new HashSet<long> { count.Id })
            .Should().BeEquivalentTo([count.Id], "its own settings changed");
    }

    // ── export ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Export_pages_have_a_total_order()
    {
        ExportReportQueryHandler.WithIdTiebreak([new SortSpec { FieldId = 8, Desc = true }]).Select(s => s.FieldId).Should().Equal(8, 3);
        ExportReportQueryHandler.WithIdTiebreak([new SortSpec { FieldId = 3, Desc = true }]).Should().HaveCount(1);
        ExportReportQueryHandler.WithIdTiebreak([]).Single().FieldId.Should().Be(3);
    }

    [Fact]
    public void Export_order_resolves_record_id_to_the_id_column()
    {
        var fields = ExportReportQueryHandler.WithRecordIdField([Plain(Dept, 6, "Text")]);
        var lookup = fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);
        RecordRepository.BuildReportOrderBy(ExportReportQueryHandler.WithIdTiebreak([new SortSpec { FieldId = 6 }]), lookup, null)
            .Should().Be("f_6 ASC, Id ASC");
    }
}
