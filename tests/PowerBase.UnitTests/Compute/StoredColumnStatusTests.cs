using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Compute;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>The field page's stored-column status, and decision Q3 (store every Lookup / Summary / Formula field).</summary>
public class StoredColumnStatusTests
{
    [Theory]
    [InlineData(MaterializeModes.On, MaterializeStates.Ready, StoredColumnStatuses.Ready)]
    [InlineData(MaterializeModes.On, MaterializeStates.Building, StoredColumnStatuses.Building)]
    [InlineData(MaterializeModes.On, MaterializeStates.Failed, StoredColumnStatuses.Failed)]
    [InlineData(MaterializeModes.On, MaterializeStates.None, StoredColumnStatuses.Waiting)]
    [InlineData(MaterializeModes.Off, MaterializeStates.None, StoredColumnStatuses.Waiting)]
    [InlineData(MaterializeModes.NotAllowed, MaterializeStates.None, StoredColumnStatuses.NotAllowed)]
    [InlineData(MaterializeModes.Excluded, MaterializeStates.None, StoredColumnStatuses.Excluded)]
    public void Each_mode_and_state_shows_one_status(byte mode, byte state, string expected) =>
        StoredColumnStatuses.From(true, mode, state).Should().Be(expected);

    [Fact]
    public void Compute_off_hides_everything_and_an_undecided_field_is_off_when_store_all_is_turned_off()
    {
        StoredColumnStatuses.From(false, MaterializeModes.On, MaterializeStates.Ready).Should().Be(StoredColumnStatuses.Disabled);
        StoredColumnStatuses.From(true, MaterializeModes.Off, MaterializeStates.None, storeAll: false).Should().Be(StoredColumnStatuses.Off);
    }

    [Fact]
    public void Store_all_is_the_default_and_can_be_turned_off_in_config()
    {
        ComputeConfig.ReadCompute(new ConfigurationBuilder().Build()).StoreAllCalculatedFields.Should().BeTrue("decision Q3");
        ComputeConfig.ReadCompute(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Compute:StoreAllCalculatedFields"] = "false" }).Build()).StoreAllCalculatedFields.Should().BeFalse();
    }

    [Fact]
    public void Excluded_is_a_new_value_that_no_read_treats_as_switched_on()
    {
        MaterializeModes.Excluded.Should().NotBe(MaterializeModes.On);
        new[] { MaterializeModes.Off, MaterializeModes.On, MaterializeModes.NotAllowed, MaterializeModes.Excluded }.Should().OnlyHaveUniqueItems();
    }
}
