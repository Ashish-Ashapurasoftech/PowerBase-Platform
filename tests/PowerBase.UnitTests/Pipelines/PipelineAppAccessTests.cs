using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>A PowerFlow step's app selector lists only the apps whose PowerFlows the user can read: the table and field selectors that
/// follow ask for that permission, so an app the user is merely a member of led to a table list that failed to load.</summary>
public class PipelineAppAccessTests
{
    [Fact]
    public async Task OnlyAppsWithPowerFlowsRead_AreListed_InTheirOrder()
    {
        var access = Substitute.For<IAppAccessService>();
        access.RequirePermissionByAppIdAsync(2, PermissionCodes.PowerFlowsRead, Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnauthorizedActionException("no"));
        var apps = new[] { new App { Id = 1, Name = "Sales" }, new App { Id = 2, Name = "HR" }, new App { Id = 3, Name = "Ops" } };

        var readable = await PipelineAppAccess.ReadableAsync(access, apps, CancellationToken.None);

        Assert.Equal(new long[] { 1, 3 }, readable.Select(a => a.Id));
    }

    [Fact]
    public async Task AnotherFailure_IsNotSwallowed()
    {
        var access = Substitute.For<IAppAccessService>();
        access.RequirePermissionByAppIdAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => PipelineAppAccess.ReadableAsync(access, [new App { Id = 1 }], CancellationToken.None));
    }
}
