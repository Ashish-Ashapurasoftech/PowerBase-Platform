using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport.OData;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Reports;

public class SearchSlotResolverTests
{
    /// <summary>Stores slots on the fields the way the application does (once, via the allocator), then returns the list.</summary>
    internal static List<AppField> Slotted(List<AppField> fields)
    {
        foreach (var (fieldId, slot) in SearchSlotResolver.AllocateMissingSlots(fields))
            fields.Single(f => f.Id == fieldId).SearchSlot = slot;
        return fields;
    }

    [Fact]
    public void Two_different_tables_sharing_fid_3_map_to_different_typed_slots()
    {
        // Table A: f_3 is Number
        var tableAFields = new List<AppField>
        {
            new() { Id = 1, Fid = 1, TypeCode = "Text", IsSearchable = true, IsFilterable = true },
            new() { Id = 2, Fid = 2, TypeCode = "Date", IsSearchable = true, IsFilterable = true },
            new() { Id = 3, Fid = 3, TypeCode = "Number", IsSearchable = true, IsFilterable = true }
        };

        // Table B: f_3 is Date
        var tableBFields = new List<AppField>
        {
            new() { Id = 10, Fid = 1, TypeCode = "Text", IsSearchable = true, IsFilterable = true },
            new() { Id = 11, Fid = 2, TypeCode = "Text", IsSearchable = true, IsFilterable = true },
            new() { Id = 12, Fid = 3, TypeCode = "Date", IsSearchable = true, IsFilterable = true }
        };

        // Table C: f_3 is Text
        var tableCFields = new List<AppField>
        {
            new() { Id = 20, Fid = 1, TypeCode = "Number", IsSearchable = true, IsFilterable = true },
            new() { Id = 21, Fid = 2, TypeCode = "Boolean", IsSearchable = true, IsFilterable = true },
            new() { Id = 22, Fid = 3, TypeCode = "Text", IsSearchable = true, IsFilterable = true }
        };

        Slotted(tableAFields); Slotted(tableBFields); Slotted(tableCFields);

        var fieldA3 = tableAFields[2];
        var fieldB3 = tableBFields[2];
        var fieldC3 = tableCFields[2];

        var slotA = SearchSlotResolver.ResolveSlotName(fieldA3, tableAFields);
        var slotB = SearchSlotResolver.ResolveSlotName(fieldB3, tableBFields);
        var slotC = SearchSlotResolver.ResolveSlotName(fieldC3, tableCFields);

        // Assert they map to distinct typed slots without collision!
        slotA.Should().Be("num_01");
        slotB.Should().Be("date_01");
        slotC.Should().Be("str_01");
    }

    [Fact]
    public void ODataFilterBuilder_emits_native_number_comparison_without_quotes()
    {
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 3, TypeCode = "Number", IsSearchable = true, IsFilterable = true }
        };

        Slotted(fields);

        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 3, Operator = "gt", Value = "150.75" } }]
        };

        var odata = ODataFilterBuilder.Build(tree, fields);

        // Must output: num_01 gt 150.75 (NOT '150.75' string!)
        odata.Should().Be("num_01 gt 150.75");
    }

    [Fact]
    public void ODataFilterBuilder_emits_native_date_comparison_with_iso_timestamp()
    {
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 3, TypeCode = "Date", IsSearchable = true, IsFilterable = true }
        };

        Slotted(fields);

        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 3, Operator = "gte", Value = "2026-10-22" } }]
        };

        var odata = ODataFilterBuilder.Build(tree, fields);

        // Must output ISO timestamp on date_01
        odata.Should().Contain("date_01 ge 2026-10-22T00:00:00.000Z");
    }

    [Fact]
    public void ODataFilterBuilder_emits_native_boolean_comparison()
    {
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 5, TypeCode = "Boolean", IsSearchable = true, IsFilterable = true }
        };

        Slotted(fields);

        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 5, Operator = "eq", Value = "true" } }]
        };

        var odata = ODataFilterBuilder.Build(tree, fields);

        odata.Should().Be("bool_01 eq true");
    }

    [Fact]
    public void Parsing_helpers_handle_diverse_types_safely()
    {
        SearchSlotResolver.ParseDouble("123.45").Should().Be(123.45);
        SearchSlotResolver.ParseDouble(999).Should().Be(999.0);
        SearchSlotResolver.ParseDouble(null).Should().BeNull();

        var dto = SearchSlotResolver.ParseDateTimeOffset("2026-10-22T12:00:00Z");
        dto.Should().NotBeNull();
        dto!.Value.Year.Should().Be(2026);

        SearchSlotResolver.ParseBoolean("true").Should().BeTrue();
        SearchSlotResolver.ParseBoolean("1").Should().BeTrue();
        SearchSlotResolver.ParseBoolean("false").Should().BeFalse();
        SearchSlotResolver.ParseBoolean("0").Should().BeFalse();
    }

    [Fact]
    public void Table_with_900_columns_where_only_30_are_search_or_filter_enabled_stays_in_shared_shard()
    {
        var fields = new List<AppField>();
        // 900 total fields
        for (int i = 1; i <= 900; i++)
        {
            // Only 30 fields are IsFilterable / IsSearchable
            bool isEligible = i <= 30;
            fields.Add(new AppField
            {
                Id = i,
                Fid = i,
                TypeCode = i % 2 == 0 ? "Number" : "Text",
                IsFilterable = isEligible,
                IsSearchable = isEligible
            });
        }

        Slotted(fields);

        // Even though table has 900 columns in SQL, only 30 are active in search
        SearchSlotResolver.IsWideTable(fields).Should().BeFalse();
        SearchSlotResolver.GetTableIndexName(1, 101, fields, "pb-shard-001").Should().Be("pb-shard-001");

        // The 30 active fields take up slots gracefully
        var slotMap = SearchSlotResolver.BuildSlotMap(fields);
        slotMap[2].Should().Be("num_01");
        slotMap[4].Should().Be("num_02");
        slotMap[1].Should().Be("str_01");

        // An inactive field does NOT occupy slots in the shared index
        slotMap[100].Should().Be("f_100");
    }

    [Fact]
    public void Table_with_over_150_search_or_filter_enabled_columns_is_classified_as_wide_and_routes_to_dedicated_index()
    {
        var fields = new List<AppField>();
        // 200 active fields
        for (int i = 1; i <= 200; i++)
        {
            fields.Add(new AppField
            {
                Id = i,
                Fid = i,
                TypeCode = "Number",
                IsFilterable = true,
                IsSearchable = true
            });
        }

        SearchSlotResolver.IsWideTable(fields).Should().BeTrue();
        SearchSlotResolver.GetTableIndexName(1, 102, fields, "pb-shard-001").Should().Be("pb-tbl-102");

        // Wide table maps directly to f_{fid} for its dedicated index
        var slotMap = SearchSlotResolver.BuildSlotMap(fields);
        slotMap[1].Should().Be("f_1");
        slotMap[150].Should().Be("f_150");
        slotMap[200].Should().Be("f_200");

        // OData translates directly on f_{fid} with native numeric comparisons
        Slotted(fields);

        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 150, Operator = "gt", Value = "999.5" } }]
        };
        var odata = ODataFilterBuilder.Build(tree, fields);
        odata.Should().Be("f_150 gt 999.5");
    }

    [Fact]
    public void ODataFilterBuilder_emits_day_range_for_date_eq()
    {
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 10, TypeCode = "Date", IsSearchable = true, IsFilterable = true }
        };

        Slotted(fields);

        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 10, Operator = "date_eq", Value = "2026-10-05" } }]
        };

        var odata = ODataFilterBuilder.Build(tree, fields);
        odata.Should().Be("(date_01 ge 2026-10-05T00:00:00.000Z and date_01 lt 2026-10-06T00:00:00.000Z)");
    }

    [Fact]
    public void ODataFilterBuilder_emits_regex_for_endsWith_and_wildcard()
    {
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 7, TypeCode = "Text", IsSearchable = true, IsFilterable = true }
        };

        Slotted(fields);

        var endsWithTree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "endsWith", Value = "Profiling" } }]
        };
        var endsWithOdata = ODataFilterBuilder.Build(endsWithTree, fields);
        endsWithOdata.Should().Be("search.ismatch('+/.*profiling/', 'str_01')");

        var wildcardTree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "wildcard", Value = "Source*Profiling" } }]
        };
        var wildcardOdata = ODataFilterBuilder.Build(wildcardTree, fields);
        wildcardOdata.Should().Be("search.ismatch('/Source.*Profiling/', 'str_01')");
    }

    [Fact]
    public void ODataFilterBuilder_CanExpress_validates_field_types_against_text_operators()
    {
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 3, TypeCode = "Number", IsSearchable = true, IsFilterable = true },
            new() { Id = 2, Fid = 7, TypeCode = "Text", IsSearchable = true, IsFilterable = true }
        };

        Slotted(fields);

        // Text operator on number field must be rejected
        var invalidTree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 3, Operator = "contains", Value = "123" } }]
        };
        ODataFilterBuilder.CanExpress(invalidTree, fields).Should().BeFalse();

        // Text operator on string field must be accepted
        var validTree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "contains", Value = "Task" } }]
        };
        ODataFilterBuilder.CanExpress(validTree, fields).Should().BeTrue();
    }
}
