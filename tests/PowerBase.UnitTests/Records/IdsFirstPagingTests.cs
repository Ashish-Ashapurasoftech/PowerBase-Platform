using FluentAssertions;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.UnitTests.Records;

/// <summary>When a report page is read Ids-first: only pages deep enough to make SQL walk tens of thousands of rows.</summary>
public class IdsFirstPagingTests
{
    [Theory]
    [InlineData(0, 200, false)]
    [InlineData(9_800, 200, false)]
    [InlineData(10_000, 200, true)]
    [InlineData(99_800, 200, true)]
    [InlineData(10_000, 1_000, true)]
    [InlineData(10_000, 1_001, false)]   // too many Ids for one parameter list
    [InlineData(10_000, 0, false)]
    public void Only_deep_pages_of_a_sane_size_read_ids_first(int offset, int pageSize, bool expected) =>
        RecordRepository.UseIdsFirstPaging(offset, pageSize).Should().Be(expected);
}
