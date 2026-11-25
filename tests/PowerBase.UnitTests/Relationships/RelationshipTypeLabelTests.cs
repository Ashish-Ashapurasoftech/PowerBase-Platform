using FluentAssertions;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

/// <summary>Reference, Lookup and Summary fields are named the Quickbase way: the type they are shown as (their saved
/// resultTypeCode) with the tag in brackets — "Currency (Lookup)".</summary>
public class RelationshipTypeLabelTests
{
    private static AppField Field(string typeCode, string? settings = null) =>
        new() { Id = 1, Fid = 7, Name = "F", TypeCode = typeCode, Settings = settings };

    [Theory]
    [InlineData("Lookup", """{"sourceFid":6,"resultTypeCode":"Currency"}""", "Currency (Lookup)")]
    [InlineData("Summary", """{"function":"Count","resultTypeCode":"Number"}""", "Number (Summary)")]
    [InlineData("Reference", """{"relationshipId":4,"resultTypeCode":"Text"}""", "Text (Reference)")]
    [InlineData("Lookup", """{"ResultTypeCode":"Date"}""", "Date (Lookup)")]                       // any casing
    [InlineData("Lookup", """{"resultTypeCode":"Reference"}""", "Number (Lookup)")]                 // shows a Record ID#
    public void Of_TaggedFieldWithSavedType_IsTypeAndTag(string typeCode, string settings, string expected) =>
        RelationshipTypeLabel.Of(Field(typeCode, settings)).Should().Be(expected);

    [Theory]
    [InlineData("Reference", """{"relationshipId":4}""")]
    [InlineData("Lookup", null)]
    [InlineData("Summary", "not json")]
    [InlineData("Summary", "[1,2]")]
    public void Of_TaggedFieldWithoutSavedType_IsTheTag(string typeCode, string? settings) =>
        RelationshipTypeLabel.Of(Field(typeCode, settings)).Should().Be(typeCode);

    [Theory]
    [InlineData("Currency", """{"resultTypeCode":"Text"}""")]
    [InlineData("Formula_Number", null)]
    [InlineData("Text", null)]
    public void Of_OtherField_IsItsOwnTypeCode(string typeCode, string? settings)
    {
        RelationshipTypeLabel.Of(Field(typeCode, settings)).Should().Be(typeCode);
        RelationshipTypeLabel.ValueType(typeCode, settings).Should().BeNull();
    }
}
