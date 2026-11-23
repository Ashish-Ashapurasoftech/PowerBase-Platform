using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using PowerBase.Application.Compute;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Search;

/// <summary>
/// A Lookup / Summary / Formula keeps the type of the value it shows in its Settings. Its search slot must be of that family, because a
/// Ready stored field is indexed and queried as a stand-in of that type (c_{fid}); a Number-valued Summary in a string slot was never
/// mapped, so the AI index could not be used for it.
/// </summary>
public class ComputedFieldSlotFamilyTests
{
    private const string CountSummary =
        """{"relationshipId":1,"childTableId":2,"referenceFid":7,"function":"Count","targetFid":null,"targetTypeCode":null,"resultTypeCode":"Number"}""";
    private const string TextLookup =
        """{"relationshipId":1,"referenceFid":7,"sourceTableId":1,"sourceFid":8,"sourceTypeCode":"Text","resultTypeCode":"Text"}""";
    private const string DateLookup =
        """{"relationshipId":1,"referenceFid":7,"sourceTableId":1,"sourceFid":9,"sourceTypeCode":"Date","resultTypeCode":"Date"}""";
    private const string BoolFormula = """{"expression":"[Quantity] > 1"}""";

    private static AppField F(long id, int fid, string type, string? settings = null, string? slot = null) =>
        new() { Id = id, AppTableId = 1, Fid = fid, TypeCode = type, Settings = settings, IsSearchable = true, IsFilterable = true, SearchSlot = slot };

    [Fact]
    public void SlotTypeCode_SummaryThatCounts_IsNumber()
    {
        SearchSlotResolver.SlotTypeCode(F(1, 10, "Summary", CountSummary)).Should().Be("Number");
        SearchSlotResolver.SlotTypeCode(F(2, 11, "Lookup", DateLookup)).Should().Be("Date");
        SearchSlotResolver.SlotTypeCode(F(3, 12, "Lookup", TextLookup)).Should().Be("Text");
    }

    [Fact]
    public void SlotTypeCode_ComputedWithoutKnownValueType_FallsBackToTypeCode()
    {
        SearchSlotResolver.SlotTypeCode(F(1, 10, "Summary", settings: null)).Should().Be("Summary");
        SearchSlotResolver.SlotTypeCode(F(2, 11, "Text")).Should().Be("Text");
        SearchSlotResolver.SlotTypeCode(F(3, 12, "Reference", """{"relationshipId":1,"parentTableId":1}""")).Should().Be("Reference");
    }

    [Fact]
    public void Allocate_NumberSummaryAndDateLookup_GetNumericAndDateSlots()
    {
        var fields = new List<AppField>
        {
            F(1, 10, "Summary", CountSummary),
            F(2, 11, "Lookup", DateLookup),
            F(3, 12, "Lookup", TextLookup),
            F(4, 13, "Number"),
        };

        var assigned = SearchSlotResolver.AllocateMissingSlots(fields).ToDictionary(a => a.FieldId, a => a.Slot);

        assigned[1].Should().Be("num_01", "a counting Summary shows a Number");
        assigned[4].Should().Be("num_02");
        assigned[2].Should().Be("date_01");
        assigned[3].Should().Be("str_01");
    }

    [Fact]
    public void Allocate_NumberSummaryHoldingAStringSlot_MovesToANumericSlot()
    {
        // What fields allocated before this fix look like.
        var fields = new List<AppField> { F(1, 10, "Summary", CountSummary, slot: "str_05") };

        var assignment = SearchSlotResolver.AllocateMissingSlots(fields).Single();

        assignment.Slot.Should().StartWith("num_");
        assignment.OldSlot.Should().Be("str_05");
    }

    [Fact]
    public void Allocate_AlreadyInTheRightFamily_IsLeftAlone()
    {
        var fields = new List<AppField> { F(1, 10, "Summary", CountSummary, slot: "num_03") };

        SearchSlotResolver.AllocateMissingSlots(fields).Should().BeEmpty();
    }

    [Fact]
    public void ReadyStandIn_OfNumberSummary_IsMappedToItsNumericSlot()
    {
        // The indexer, the document reader and the report queries all see a Ready field as a stand-in of its value type.
        var summary = F(1, 10, "Summary", CountSummary, slot: "num_01");
        var fields = MaterializedReadFields.Substitute([summary], new HashSet<long> { 1 });

        var info = SearchTableSlotInfo.From(1, fields);

        info.SlotMap.Should().ContainKey(10);
        info.SlotMap[10].Slot.Should().Be("num_01");
        SearchSlotResolver.ResolveSlotName(fields[0], fields).Should().Be("num_01");
    }

    [Fact]
    public void ReadyStandIn_OfNumberSummaryInAStringSlot_WasNotMapped()
    {
        // The state before the fix: the string slot does not fit a Number stand-in, so the field fell back to SQL.
        var summary = F(1, 10, "Summary", CountSummary, slot: "str_05");
        var fields = MaterializedReadFields.Substitute([summary], new HashSet<long> { 1 });

        SearchTableSlotInfo.From(1, fields).SlotMap.Should().NotContainKey(10);
    }

    [Fact]
    public void FieldSyncPlan_ForNumberSummary_HasATarget()
    {
        // FieldSlotSync plans from the plain (not substituted) field list: the computed field must still have a column to write.
        var fields = new List<AppField> { F(1, 10, "Summary", CountSummary, slot: "num_01") };

        new FieldSlotSync.Plan(SearchTableSlotInfo.From(1, fields), 10, IsNullify: false).HasTarget.Should().BeTrue();
    }

    [Fact]
    public void BoolFormula_GetsABooleanSlot()
    {
        var fields = new List<AppField> { F(1, 10, "Formula_Bool", BoolFormula) };

        SearchSlotResolver.AllocateMissingSlots(fields).Single().Slot.Should().StartWith("bool_");
    }
}
