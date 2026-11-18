using FluentAssertions;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class RequestOriginRegistryTests
{
    [Fact]
    public void Remember_KeepsEachTenantsOwnAddress()
    {
        var registry = new RequestOriginRegistry();
        registry.Remember(1, "https://tenant-a.example.com/");
        registry.Remember(2, "http://localhost:5159");
        registry.Get(1).Should().Be("https://tenant-a.example.com");
        registry.Get(2).Should().Be("http://localhost:5159");
        registry.Get(3).Should().BeNull();
    }

    [Theory]
    [InlineData(0, "https://a.example.com")]
    [InlineData(1, "")]
    [InlineData(1, "ftp://a.example.com")]
    [InlineData(1, "not a url")]
    public void Remember_IgnoresUnusableInput(long tenant, string origin)
    {
        var registry = new RequestOriginRegistry();
        registry.Remember(tenant, origin);
        registry.Get(tenant).Should().BeNull();
    }
}
