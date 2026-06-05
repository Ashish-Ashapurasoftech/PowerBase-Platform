using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Commands.DeleteReport;
using PowerBase.Application.Reports.Commands.UpdateReport;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Reports;

public class UpdateDeleteReportHandlerTests
{
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private UpdateReportCommandHandler CreateUpdateSut() => new(_reportRepo, _appAccessService, _appUserRepo, _appRoleRepo, _queryContext, _auditRepo);
    private DeleteReportCommandHandler CreateDeleteSut() => new(_reportRepo, _appAccessService, _auditRepo);

    private static UpdateReportCommand ValidCommand(Guid id, string name = "New Name") =>
        new(id, name, null, "Shared", [], [], null, null, "EqualValues", false, false, false, [], "Default", [], null, true, []);

    [Fact]
    public async Task UpdateReport_ValidCommand_CallsUpdate()
    {
        var id = Guid.NewGuid();
        _reportRepo.UpdateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateUpdateSut();

        await sut.HandleAsync(ValidCommand(id));

        await _reportRepo.Received(1).UpdateAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateReport_EmptyName_ThrowsValidationException()
    {
        var sut = CreateUpdateSut();

        await sut.Invoking(s => s.HandleAsync(ValidCommand(Guid.NewGuid(), "")))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateReport_NameTooLong_ThrowsValidationException()
    {
        var sut = CreateUpdateSut();

        await sut.Invoking(s => s.HandleAsync(ValidCommand(Guid.NewGuid(), new string('x', 201))))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateReport_NotFound_ThrowsNotFoundException()
    {
        var id = Guid.NewGuid();
        _reportRepo.UpdateAsync(id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateUpdateSut();

        await sut.Invoking(s => s.HandleAsync(ValidCommand(id)))
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
