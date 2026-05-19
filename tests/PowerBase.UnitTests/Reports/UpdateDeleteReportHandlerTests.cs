using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.Commands.DeleteReport;
using PowerBase.Application.Reports.Commands.UpdateReport;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Reports;

public class UpdateDeleteReportHandlerTests
{
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();

    private UpdateReportCommandHandler CreateUpdateSut() => new(_reportRepo, _fieldRepo);
    private DeleteReportCommandHandler CreateDeleteSut() => new(_reportRepo);

    [Fact]
    public async Task UpdateReport_ValidCommand_CallsUpdate()
    {
        var id = Guid.NewGuid();
        _reportRepo.GetByPublicIdAsync(id).Returns(new Report { PublicId = id, AppTableId = 1, Definition = "{}" });
        _fieldRepo.ListByTableAsync(1).Returns(new List<AppField> { new() { Id = 10 } });
        var sut = CreateUpdateSut();

        await sut.HandleAsync(new UpdateReportCommand(id, "New Name", null, "Shared", new List<long> { 10 }, null, false));

        await _reportRepo.Received(1).UpdateAsync(Arg.Is<Report>(r => r.Name == "New Name" && r.Visibility == "Shared"));
    }

    [Fact]
    public async Task UpdateReport_EmptyName_ThrowsValidationException()
    {
        var sut = CreateUpdateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateReportCommand(Guid.NewGuid(), "", null, "Personal", [], null, false)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateReport_InvalidVisibility_ThrowsValidationException()
    {
        var sut = CreateUpdateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateReportCommand(Guid.NewGuid(), "R", null, "BadValue", [], null, false)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateReport_UnknownColumnFieldId_ThrowsValidationException()
    {
        var id = Guid.NewGuid();
        _reportRepo.GetByPublicIdAsync(id).Returns(new Report { PublicId = id, AppTableId = 1, Definition = "{}" });
        _fieldRepo.ListByTableAsync(1).Returns(new List<AppField> { new() { Id = 10 } });
        var sut = CreateUpdateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateReportCommand(id, "R", null, "Personal", new List<long> { 999 }, null, false)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task DeleteReport_CallsDeleteOnRepo()
    {
        var id = Guid.NewGuid();
        var sut = CreateDeleteSut();

        await sut.HandleAsync(new DeleteReportCommand(id));

        await _reportRepo.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }
}
