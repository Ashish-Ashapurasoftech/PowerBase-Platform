using PowerBase.Application.Apps.Commands.UpdateRecordFilters;
using Xunit;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Import;

/// <summary>A filter on a checkbox must hold true/false whether the field is a real checkbox or a Lookup of one
/// (Quickbase writes both as '1'): "Program - Qualifies for QCI By Default" (a lookup) was left as "1" and showed as "Select".</summary>
public class PblFilterBooleanLookupTests
{
    private static AppField Field(string name, int fid, string typeCode, string? settings = null) =>
        new() { Name = name, Label = name, Fid = fid, TypeCode = typeCode, Settings = settings };

    private static PblFilterGroup Group(params string[] fieldNames) => new()
    {
        Logic = "or",
        Conditions = fieldNames.Select(n => new PblFilterCondition { FieldName = n, Operator = "eq", Value = "1" }).ToList(),
    };

    [Fact]
    public void Checkbox_value_one_becomes_true_for_a_real_checkbox_and_for_a_lookup_of_one()
    {
        var fields = new List<AppField>
        {
            Field("Author_Signed", 29, "Boolean"),
            Field("Program_Qualifies", 27, "Lookup", "{\"resultTypeCode\":\"Boolean\",\"sourceTypeCode\":\"Boolean\"}"),
        };

        var group = PblPermissionsApplier.ToFilterGroup(Group("Author_Signed", "Program_Qualifies"), fields, out var problem);

        Assert.Null(problem);
        Assert.NotNull(group);
        Assert.All(group!.Nodes, n => Assert.Equal("true", n.Condition!.Value));
    }

    [Fact]
    public void Lookup_without_a_result_type_falls_back_to_its_source_type()
    {
        var fields = new List<AppField> { Field("Flag", 5, "Lookup", "{\"sourceTypeCode\":\"Boolean\"}") };

        var group = PblPermissionsApplier.ToFilterGroup(Group("Flag"), fields, out _);

        Assert.Equal("true", group!.Nodes.Single().Condition!.Value);
    }

    [Fact]
    public void Lookup_of_a_number_keeps_its_value()
    {
        var fields = new List<AppField> { Field("Count", 6, "Lookup", "{\"resultTypeCode\":\"Number\"}") };

        var group = PblPermissionsApplier.ToFilterGroup(Group("Count"), fields, out _);

        Assert.Equal("1", group!.Nodes.Single().Condition!.Value);
    }
}
