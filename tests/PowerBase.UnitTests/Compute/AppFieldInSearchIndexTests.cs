using FluentAssertions;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>
/// A stored Lookup / Summary / Formula may be read from the search index only when it is stored, Ready, Indexed, and the index holds the
/// very version that was built. A rebuild bumps MaterializeVersion, which alone ends "in the index" without touching SearchIndexState.
/// </summary>
public class AppFieldInSearchIndexTests
{
    private static AppField Field(byte mode = MaterializeModes.On, byte state = MaterializeStates.Ready, int version = 3,
        byte searchState = SearchIndexStates.Indexed, int? searchVersion = 3) => new()
    {
        TypeCode = "Summary",
        MaterializeMode = mode,
        MaterializeState = state,
        MaterializeVersion = version,
        SearchIndexState = searchState,
        SearchIndexVersion = searchVersion,
    };

    [Fact]
    public void IsInSearchIndex_ReadyAndIndexedForTheBuiltVersion_IsTrue() =>
        Field().IsInSearchIndex.Should().BeTrue();

    [Fact]
    public void IsInSearchIndex_StillIndexing_IsFalse() =>
        Field(searchState: SearchIndexStates.Indexing).IsInSearchIndex.Should().BeFalse();

    [Fact]
    public void IsInSearchIndex_RebuiltSinceItWasIndexed_IsFalse() =>
        Field(version: 4, searchVersion: 3).IsInSearchIndex.Should().BeFalse("the index holds the values of the previous build");

    [Theory]
    [InlineData(MaterializeStates.Building)]
    [InlineData(MaterializeStates.Failed)]
    [InlineData(MaterializeStates.None)]
    public void IsInSearchIndex_NotReady_IsFalse(byte state) =>
        Field(state: state).IsInSearchIndex.Should().BeFalse();

    [Theory]
    [InlineData(MaterializeModes.Off)]
    [InlineData(MaterializeModes.NotAllowed)]
    [InlineData(MaterializeModes.Excluded)]
    public void IsInSearchIndex_NotStored_IsFalse(byte mode) =>
        Field(mode: mode).IsInSearchIndex.Should().BeFalse();

    [Theory]
    [InlineData(SearchIndexStates.None)]
    [InlineData(SearchIndexStates.Failed)]
    public void IsInSearchIndex_NotIndexed_IsFalse(byte searchState) =>
        Field(searchState: searchState).IsInSearchIndex.Should().BeFalse();

    [Fact]
    public void NewField_IsExactAndNotInTheIndex()
    {
        var field = new AppField();
        field.SearchExact.Should().BeTrue();
        field.IsInSearchIndex.Should().BeFalse();
    }
}
