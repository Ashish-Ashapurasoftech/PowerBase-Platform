using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.Commands.DeleteReport;
using PowerBase.Application.Reports.Commands.UpdateReport;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Reports;

public class UpdateDeleteReportHandlerTests
{
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private UpdateReportCommandHandler CreateUpdateSut() => new(_reportRepo, _appAccessService);
    private DeleteReportCommandHandler CreateDeleteSut() => new(_reportRepo, _appAccessService);

    [Fact]
    public async Task UpdateReport_ValidCommand_CallsUpdate()
    {
        var id = Guid.NewGuid();
        _reportRepo.UpdateAsync(id, "New Name", null, Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateUpdateSut();

        await sut.HandleAsync(new UpdateReportCommand(id, "New Name", null));

        await _reportRepo.Received(1).UpdateAsync(id, "New Name", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateReport_EmptyName_ThrowsValidationException()
    {
        var sut = CreateUpdateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateReportCommand(Guid.NewGuid(), "", null)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateReport_NameTooLong_ThrowsValidationException()
    {
        var sut = CreateUpdateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateReportCommand(Guid.NewGuid(), new string('x', 201), null)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateReport_NotFound_ThrowsNotFoundException()
    {
        var id = Guid.NewGuid();
        _reportRepo.UpdateAsync(id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateUpdateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateReportCommand(id, "Name", null)))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task DeleteReport_CallsDeleteOnRepo()
    {
        var id = Guid.NewGuid();
        _reportRepo.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateDeleteSut();

        await sut.HandleAsync(new DeleteReportCommand(id));

        await _reportRepo.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteReport_NotFound_ThrowsNotFoundException()
    {
        var id = Guid.NewGuid();
        _reportRepo.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateDeleteSut();

        await sut.Invoking(s => s.HandleAsync(new DeleteReportCommand(id)))
            .Should().ThrowAsync<NotFoundException>();
    }
}
