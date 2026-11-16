using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.API.Attributes;
using PowerBase.API.Controllers;
using PowerBase.Application.Apps.Commands.DismissAppCopy;
using PowerBase.Application.Apps.Queries.ListMyAppCopies;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Constants;

namespace PowerBase.UnitTests.Apps;

/// <summary>The app list's copies with data: listing the caller's copies and dismissing one.</summary>
public class AppCopiesListTests
{
    private const long USER_ID = 7;

    private readonly IAppCopyRepository _copyRepo = Substitute.For<IAppCopyRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    public AppCopiesListTests()
    {
        _queryContext.UserId.Returns(USER_ID);
    }

    private static AppCopyRunSummary Run(string status) => new(
        Guid.NewGuid(), "Copy of Big App", Guid.NewGuid(), "Big App", status, 40, "Copying records: Orders 100,000 of 500,000",
        100_000, 500_000, true, 95, null);

    [Fact]
    public async Task List_ReturnsTheCallersCopies()
    {
        var runs = new[] { Run("Running"), Run("Failed") };
        _copyRepo.ListDataCopiesAsync(USER_ID, Arg.Any<CancellationToken>()).Returns(runs);

        var result = await new ListMyAppCopiesQueryHandler(_copyRepo, _queryContext).HandleAsync();

        result.Should().BeEquivalentTo(runs);
        await _copyRepo.Received(1).ListDataCopiesAsync(USER_ID, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_RemovesCopiesAServerRestartCutOffFirst()
    {
        _copyRepo.ListDataCopiesAsync(USER_ID, Arg.Any<CancellationToken>()).Returns(Array.Empty<AppCopyRunSummary>());

        await new ListMyAppCopiesQueryHandler(_copyRepo, _queryContext).HandleAsync();

        Received.InOrder(() =>
        {
            _copyRepo.DiscardStaleDataCopiesAsync(Arg.Any<CancellationToken>());
            _copyRepo.ListDataCopiesAsync(USER_ID, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task List_StaleCleanUpFails_StillLists()
    {
        _copyRepo.DiscardStaleDataCopiesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db busy"));
        _copyRepo.ListDataCopiesAsync(USER_ID, Arg.Any<CancellationToken>()).Returns(new[] { Run("Running") });

        var result = await new ListMyAppCopiesQueryHandler(_copyRepo, _queryContext).HandleAsync();

        result.Should().HaveCount(1);
    }

    // The app list asks for the copies on every visit; the default "User" tenant role has apps:read but not apps:create, so
    // apps:create here would show those users an "Access Denied" toast each time.
    [Theory]
    [InlineData(nameof(AppCopiesController.List))]
    [InlineData(nameof(AppCopiesController.Dismiss))]
    public void Endpoints_NeedOnlyAppsRead(string action)
    {
        var permission = typeof(AppCopiesController).GetMethod(action)!.GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(RequirePermissionAttribute))
            .ConstructorArguments.Single().Value;

        permission.Should().Be(PermissionCodes.AppsRead);
        PermissionCodes.DefaultUserPermissions.Should().Contain(PermissionCodes.AppsRead);
    }

    [Fact]
    public async Task Dismiss_HidesTheCopyForTheCaller()
    {
        var copyId = Guid.NewGuid();

        await new DismissAppCopyCommandHandler(_copyRepo, _queryContext).HandleAsync(copyId);

        await _copyRepo.Received(1).DismissDataCopyAsync(copyId, USER_ID, Arg.Any<CancellationToken>());
    }
}
