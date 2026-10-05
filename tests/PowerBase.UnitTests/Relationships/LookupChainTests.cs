using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

public class LookupChainTests
{
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();

    private static AppField Field(int fid, string typeCode, string? settings = null) => new()
    {
        Id = fid, Fid = fid, Name = $"F{fid}", TypeCode = typeCode, Settings = settings,
    };

    private static AppField LookupOf(int fid, long sourceTable, int sourceFid, string sourceType) =>
        Field(fid, "Lookup", $"{{\"sourceTableId\":{sourceTable},\"sourceFid\":{sourceFid},\"sourceTypeCode\":\"{sourceType}\"}}");

    /// <summary>Tables 1..n each hold one lookup (fid 10+i) reading the next table's; the last reads a Text field.</summary>
    private AppField BuildChain(int lookups)
    {
        _fieldRepo.ListByTableAsync(1000, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(5, "Text") });
        AppField? head = null;
        for (var i = lookups; i >= 1; i--)
        {
            var lookup = i == lookups ? LookupOf(10 + i, 1000, 5, "Text") : LookupOf(10 + i, 100 + i + 1, 10 + i + 1, "Text");
            if (i > 1) _fieldRepo.ListByTableAsync(100 + i, Arg.Any<CancellationToken>()).Returns(new List<AppField> { lookup });
            head = lookup;
        }
        return head!;
    }

    [Fact]
    public async Task Resolve_plain_field_returns_its_own_type_and_zero_length()
    {
        var (type, length) = await LookupChain.ResolveAsync(Field(5, "Number"), _fieldRepo, default);

        type.Should().Be("Number");
        length.Should().Be(0);
    }

    [Fact]
    public async Task Resolve_lookup_of_lookup_returns_the_underlying_type()
    {
        _fieldRepo.ListByTableAsync(88, Arg.Any<CancellationToken>()).Returns(new List<AppField> { Field(5, "Date") });
        var inner = LookupOf(6, 88, 5, "Date");
        _fieldRepo.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(new List<AppField> { inner });

        var (type, length) = await LookupChain.ResolveAsync(LookupOf(11, 99, 6, "Date"), _fieldRepo, default);

        type.Should().Be("Date");
        length.Should().Be(2);
    }

    [Fact]
    public async Task ResolveForNewLookup_allows_a_chain_up_to_the_cap()
    {
        var source = BuildChain(LookupChain.MaxLength - 1);   // the new lookup makes it MaxLength

        var type = await LookupChain.ResolveForNewLookupAsync(source, _fieldRepo, default);

        type.Should().Be("Text");
    }

    [Fact]
    public async Task ResolveForNewLookup_refuses_a_chain_longer_than_the_cap()
    {
        var source = BuildChain(LookupChain.MaxLength);

        var act = () => LookupChain.ResolveForNewLookupAsync(source, _fieldRepo, default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ResolveForNewLookup_of_a_plain_field_returns_its_type()
    {
        var type = await LookupChain.ResolveForNewLookupAsync(Field(5, "Number"), _fieldRepo, default);

        type.Should().Be("Number");
    }
}
