using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>Deleting several imports of an app at once: all or nothing, only this app's, by the same right as deleting one.</summary>
public class ImportBulkDeleteTests
{
    private static readonly Guid AppId = Guid.NewGuid();
    private static readonly Guid TableA = Guid.NewGuid();
    private static readonly Guid TableB = Guid.NewGuid();

    private readonly IAppRepository _apps = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IImportDefinitionRepository _definitions = Substitute.For<IImportDefinitionRepository>();
    private readonly IQueryContext _user = Substitute.For<IQueryContext>();
    private readonly List<ImportDefinition> _inApp;
    private readonly DeleteImportDefinitionsHandler _sut;

    public ImportBulkDeleteTests()
    {
        _apps.GetIdByPublicIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(1L);
        _tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, PublicId = TableA });
        _tables.GetByIdAsync(12, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 12, PublicId = TableB });
        _user.UserId.Returns(5L);
        _inApp = [Def(1, 11), Def(2, 11), Def(3, 12)];
        _definitions.ListEntitiesByAppAsync(1, Arg.Any<CancellationToken>()).Returns(_ => _inApp);
        _sut = new DeleteImportDefinitionsHandler(_apps, _tables, _access, _definitions, _user);
    }

    private static ImportDefinition Def(long id, long destination) => new() { Id = id, PublicId = Guid.NewGuid(), DestinationTableId = destination, AppId = 1, Name = "n" + id };

    private Task<int> Delete(params ImportDefinition[] which) => _sut.HandleAsync(AppId, which.Select(d => d.PublicId).ToList(), default);

    [Fact]
    public async Task The_chosen_imports_are_deleted_in_one_statement_and_counted()
    {
        var deleted = await Delete(_inApp[0], _inApp[2]);

        deleted.Should().Be(2);
        await _definitions.Received(1).DeleteManyAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.OrderBy(i => i).SequenceEqual(new long[] { 1, 3 })), 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_right_to_add_records_is_checked_once_for_each_table_involved()
    {
        await Delete(_inApp[0], _inApp[1], _inApp[2]);

        await _access.Received(1).RequirePermissionByTablePublicIdAsync(TableA, PermissionCodes.RecordsCreate, Arg.Any<CancellationToken>());
        await _access.Received(1).RequirePermissionByTablePublicIdAsync(TableB, PermissionCodes.RecordsCreate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task If_one_table_is_refused_nothing_is_deleted()
    {
        _access.RequirePermissionByTablePublicIdAsync(TableB, PermissionCodes.RecordsCreate, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedActionException("add records to the table"));

        var act = () => Delete(_inApp[0], _inApp[2]);

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await _definitions.DidNotReceiveWithAnyArgs().DeleteManyAsync(default!, default, default);
    }

    [Fact]
    public async Task An_id_from_another_app_is_not_found_and_nothing_is_done_for_it()
    {
        var stranger = Def(99, 11);

        var deleted = await _sut.HandleAsync(AppId, [stranger.PublicId, _inApp[0].PublicId], default);

        deleted.Should().Be(1);
        await _definitions.Received(1).DeleteManyAsync(Arg.Is<IReadOnlyCollection<long>>(ids => ids.Single() == 1), 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Someone_outside_the_app_deletes_nothing()
    {
        _access.RequireMembershipByAppPublicIdAsync(AppId, Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new UnauthorizedActionException("see this app"));

        var act = () => Delete(_inApp[0]);

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await _definitions.DidNotReceiveWithAnyArgs().DeleteManyAsync(default!, default, default);
    }

    [Fact]
    public async Task Nothing_chosen_does_nothing_and_asks_nothing()
    {
        (await _sut.HandleAsync(AppId, [], default)).Should().Be(0);

        await _definitions.DidNotReceiveWithAnyArgs().ListEntitiesByAppAsync(default, default);
    }

    [Fact]
    public async Task The_same_id_twice_counts_once()
    {
        var deleted = await _sut.HandleAsync(AppId, [_inApp[0].PublicId, _inApp[0].PublicId], default);

        deleted.Should().Be(1);
    }

    [Fact]
    public async Task A_very_long_list_is_refused()
    {
        var many = Enumerable.Range(0, DeleteImportDefinitionsHandler.MaxAtOnce + 1).Select(_ => Guid.NewGuid()).ToList();

        var act = () => _sut.HandleAsync(AppId, many, default);

        await act.Should().ThrowAsync<ValidationException>();
    }
}
