using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Queries.SharedValues;
using PowerBase.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerBase.UnitTests.Fields;

/// <summary>
/// A Shared-values field feeds another field's dropdown, possibly from an encrypted app: the values must come back
/// as plain text, and a value that cannot be decrypted is left out rather than offered as ciphertext.
/// </summary>
public class SharedFieldValuesQueryTests
{
    private const int Fid = 6;

    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly AppTable _table = new() { Id = 5, AppId = 3, PublicId = Guid.NewGuid() };

    private GetSharedFieldValuesQueryHandler Handler() => new(_tableRepo, _fieldRepo, _recordRepo);

    private void GivenField(bool shared)
    {
        _tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 100, PublicId = Guid.NewGuid(), Fid = Fid, TypeCode = "Text", IsSharedValues = shared },
        });
    }

    [Fact]
    public async Task Values_are_read_with_undecryptable_ones_dropped()
    {
        GivenField(shared: true);
        _recordRepo.GetDistinctFieldValuesAsync(_table, Arg.Any<AppField>(), Arg.Any<int>(), null, Arg.Any<CancellationToken>(), null, true)
            .Returns((new List<string> { "CEO", "Manager" } as IReadOnlyList<string>, false));

        var result = await Handler().HandleAsync(_table.PublicId, Fid, 1000);

        result.Values.Should().Equal("CEO", "Manager");
        await _recordRepo.Received(1).GetDistinctFieldValuesAsync(_table, Arg.Any<AppField>(), Arg.Any<int>(), null,
            Arg.Any<CancellationToken>(), null, true);
    }

    [Fact]
    public async Task A_field_that_is_not_shared_offers_nothing()
    {
        GivenField(shared: false);

        var result = await Handler().HandleAsync(_table.PublicId, Fid, 1000);

        result.Values.Should().BeEmpty();
        await _recordRepo.DidNotReceiveWithAnyArgs().GetDistinctFieldValuesAsync(default!, default!, default);
    }
}
