/*
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.Commands.SetDefaultReport;

namespace PowerBase.UnitTests.Reports;

public class SetDefaultReportHandlerTests
{
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private SetDefaultReportCommandHandler CreateSut() =>
        new(_reportRepo, _appAccessService);

    [Fact]
    public async Task SetDefault_ValidCommand_CallsSetDefaultOnRepo()
    {
        var tableId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var sut = CreateSut();

        await sut.HandleAsync(new SetDefaultReportCommand(tableId, reportId));

        await _reportRepo.Received(1).SetDefaultAsync(tableId, reportId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDefault_ValidCommand_RequiresAdminAccess()
    {
        var tableId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var sut = CreateSut();

        await sut.HandleAsync(new SetDefaultReportCommand(tableId, reportId));

        await _appAccessService.Received(1).RequireByReportPublicIdAsync(
            reportId, AppAccess.Admin, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDefault_AccessDenied_DoesNotCallRepo()
    {
        var tableId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        _appAccessService
            .RequireByReportPublicIdAsync(reportId, AppAccess.Admin, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new UnauthorizedAccessException("Forbidden")));
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new SetDefaultReportCommand(tableId, reportId)))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        await _reportRepo.DidNotReceive()
            .SetDefaultAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}

*/
