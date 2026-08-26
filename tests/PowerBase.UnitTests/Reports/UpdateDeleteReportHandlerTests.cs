using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Commands.DeleteReport;
using PowerBase.Application.Reports.Commands.UpdateReport;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Reports;

public class UpdateDeleteReportHandlerTests
{
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private static ReportConfigValidatorRegistry MakeConfigValidatorRegistry() =>
        new([new TableReportConfigValidator(), new SummaryReportConfigValidator(), new ChartReportConfigValidator()]);

    private UpdateReportCommandHandler CreateUpdateSut() =>
        new(_reportRepo, _fieldRepo, _appAccessService, _appUserRepo, _appRoleRepo, _queryContext, _auditRepo, MakeConfigValidatorRegistry());
    private DeleteReportCommandHandler CreateDeleteSut() => new(_reportRepo, _appAccessService, _auditRepo);

    private static UpdateReportCommand ValidCommand(Guid id, string name = "New Name") =>
        new(id, name, null, "Shared", [], [], null, null, "EqualValues", false, false, false, [], "Default", [], null, true, []);

    /// <summary>Handler now fetches the existing report up front (to resolve AppTableId/ReportType
    /// for the per-type validator) before doing anything else — tests that exercise the actual
    /// update path need this mocked, matching the real repo's Report shape.</summary>
    private static Report MakeExistingReport(Guid publicId, long appTableId = 5, string reportType = "Table") => new()
    {
        Id = 10,
        PublicId = publicId,
        AppTableId = appTableId,
        Name = "Existing",
        ReportType = reportType,
        Visibility = "Personal",
        Definition = "{}",
        CreatedOn = DateTime.UtcNow,
    };

    [Fact]
    public async Task UpdateReport_ValidCommand_CallsUpdate()
    {
        var id = Guid.NewGuid();
        _reportRepo.GetByPublicIdAsync(id, Arg.Any<CancellationToken>()).Returns(MakeExistingReport(id));
        _fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
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
        // Handler now fetches the report up front — the real repo throws NotFoundException here
        // when the report doesn't exist (see ReportRepository.GetByPublicIdAsync), so that's what
        // this test simulates instead of relying on UpdateAsync's affected-row count.
        _reportRepo.GetByPublicIdAsync(id, Arg.Any<CancellationToken>()).Returns<Report>(_ => throw new NotFoundException("Report", id));
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
