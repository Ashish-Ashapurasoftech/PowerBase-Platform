using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Commands.UpdateDefaultReportSettings;
using PowerBase.Application.Reports.Queries.GetDefaultReportSettings;
using PowerBase.Application.Reports.Queries.ResolveDefaultReport;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Reports;

public class DefaultReportSettingsHandlerTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    public DefaultReportSettingsHandlerTests()
    {
        _queryContext.UserId.Returns(42L);
    }

    [Fact]
    public async Task GetSettings_EmptyJson_ReturnsEveryoneMode()
    {
        var tableId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>()).Returns(MakeTable(defaultReportSettings: "{}"));
        _reportRepo.ListByTableAsync(tableId, Arg.Any<CancellationToken>()).Returns([MakeReport(reportId, isDefault: true)]);
        _appRoleRepo.ListDetailsByAppIdAsync(10L, Arg.Any<CancellationToken>()).Returns([MakeRoleDetail(Guid.NewGuid())]);
        var sut = new GetDefaultReportSettingsQueryHandler(_tableRepo, _appRoleRepo, _reportRepo);

        var result = await sut.HandleAsync(new GetDefaultReportSettingsQuery(tableId));

        result.Mode.Should().Be(DefaultReportModes.Everyone);
        result.EveryoneReportId.Should().Be(reportId);
    }

    [Fact]
    public async Task UpdateSettings_EveryoneMode_UpdatesDefaultAndClearsRoleMappings()
    {
        var tableId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>()).Returns(MakeTable());
        _reportRepo.BelongsToTableAsync(tableId, reportId, Arg.Any<CancellationToken>()).Returns(true);
        var sut = new UpdateDefaultReportSettingsCommandHandler(_tableRepo, _appRoleRepo, _reportRepo, _appAccessService);

        await sut.HandleAsync(new UpdateDefaultReportSettingsCommand(
            tableId,
            DefaultReportModes.Everyone,
            reportId,
            new Dictionary<Guid, Guid?> { [Guid.NewGuid()] = Guid.NewGuid() }));

        await _reportRepo.Received(1).SetDefaultAsync(tableId, reportId, Arg.Any<CancellationToken>());
        await _tableRepo.Received(1).UpdateDefaultReportSettingsAsync(
            tableId,
            Arg.Is<string>(json => json.Contains("\"mode\":\"Everyone\"") && json.Contains("\"roleDefaults\":{}")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSettings_RoleBasedMode_PersistsRoleMappings()
    {
        var tableId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var roleReportId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>()).Returns(MakeTable());
        _reportRepo.BelongsToTableAsync(tableId, reportId, Arg.Any<CancellationToken>()).Returns(true);
        _reportRepo.BelongsToTableAsync(tableId, roleReportId, Arg.Any<CancellationToken>()).Returns(true);
        _appRoleRepo.GetByPublicIdAsync(roleId, Arg.Any<CancellationToken>()).Returns(MakeRole(roleId));
        var sut = new UpdateDefaultReportSettingsCommandHandler(_tableRepo, _appRoleRepo, _reportRepo, _appAccessService);

        await sut.HandleAsync(new UpdateDefaultReportSettingsCommand(
            tableId,
            DefaultReportModes.RoleBased,
            reportId,
            new Dictionary<Guid, Guid?> { [roleId] = roleReportId }));

        await _tableRepo.Received(1).UpdateDefaultReportSettingsAsync(
            tableId,
            Arg.Is<string>(json => json.Contains("\"mode\":\"RoleBased\"") && json.Contains(roleId.ToString()) && json.Contains(roleReportId.ToString())),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSettings_ReportFromAnotherTable_ThrowsValidationException()
    {
        var tableId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>()).Returns(MakeTable());
        _reportRepo.BelongsToTableAsync(tableId, reportId, Arg.Any<CancellationToken>()).Returns(false);
        var sut = new UpdateDefaultReportSettingsCommandHandler(_tableRepo, _appRoleRepo, _reportRepo, _appAccessService);

        await sut.Invoking(s => s.HandleAsync(new UpdateDefaultReportSettingsCommand(
                tableId,
                DefaultReportModes.Everyone,
                reportId,
                new Dictionary<Guid, Guid?>())))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateSettings_RoleFromAnotherApp_ThrowsValidationException()
    {
        var tableId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>()).Returns(MakeTable(appId: 10L));
        _reportRepo.BelongsToTableAsync(tableId, reportId, Arg.Any<CancellationToken>()).Returns(true);
        _appRoleRepo.GetByPublicIdAsync(roleId, Arg.Any<CancellationToken>()).Returns(MakeRole(roleId, appId: 99L));
        var sut = new UpdateDefaultReportSettingsCommandHandler(_tableRepo, _appRoleRepo, _reportRepo, _appAccessService);

        await sut.Invoking(s => s.HandleAsync(new UpdateDefaultReportSettingsCommand(
                tableId,
                DefaultReportModes.RoleBased,
                reportId,
                new Dictionary<Guid, Guid?> { [roleId] = reportId })))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ResolveDefault_RoleMappingExists_ReturnsRoleReport()
    {
        var tableId = Guid.NewGuid();
        var everyoneReportId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var roleReportId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>()).Returns(MakeTable(defaultReportSettings: JsonSerializer.Serialize(new DefaultReportSettingsDocument
        {
            Mode = DefaultReportModes.RoleBased,
            RoleDefaults = new Dictionary<string, string> { [roleId.ToString()] = roleReportId.ToString() },
        })));
        _reportRepo.GetDefaultByTableAsync(tableId, Arg.Any<CancellationToken>()).Returns(MakeReport(everyoneReportId, isDefault: true));
        _appUserRepo.GetUserRolePublicIdAsync(10L, 42L, Arg.Any<CancellationToken>()).Returns(roleId);
        _reportRepo.BelongsToTableAsync(tableId, roleReportId, Arg.Any<CancellationToken>()).Returns(true);
        _reportRepo.GetByPublicIdAsync(roleReportId, Arg.Any<CancellationToken>()).Returns(MakeReport(roleReportId, name: "Role Report"));
        var sut = new ResolveDefaultReportQueryHandler(_tableRepo, _appUserRepo, _reportRepo, _queryContext);

        var result = await sut.HandleAsync(new ResolveDefaultReportQuery(tableId));

        result.Id.Should().Be(roleReportId);
        result.Name.Should().Be("Role Report");
    }

    [Fact]
    public async Task ResolveDefault_UnmappedRole_ReturnsEveryoneDefault()
    {
        var tableId = Guid.NewGuid();
        var everyoneReportId = Guid.NewGuid();
        _tableRepo.GetByPublicIdAsync(tableId, Arg.Any<CancellationToken>()).Returns(MakeTable(defaultReportSettings: JsonSerializer.Serialize(new DefaultReportSettingsDocument
        {
            Mode = DefaultReportModes.RoleBased,
        })));
        _reportRepo.GetDefaultByTableAsync(tableId, Arg.Any<CancellationToken>()).Returns(MakeReport(everyoneReportId, isDefault: true, name: "Everyone Report"));
        _appUserRepo.GetUserRolePublicIdAsync(10L, 42L, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var sut = new ResolveDefaultReportQueryHandler(_tableRepo, _appUserRepo, _reportRepo, _queryContext);

        var result = await sut.HandleAsync(new ResolveDefaultReportQuery(tableId));

        result.Id.Should().Be(everyoneReportId);
        result.Name.Should().Be("Everyone Report");
    }

    private static AppTable MakeTable(long appId = 10L, string defaultReportSettings = "{}") => new()
    {
        Id = 1L,
        PublicId = Guid.NewGuid(),
        AppId = appId,
        DefaultReportSettings = defaultReportSettings,
    };

    private static AppRole MakeRole(Guid publicId, long appId = 10L) => new()
    {
        Id = 2L,
        PublicId = publicId,
        AppId = appId,
        Name = "Viewer",
    };

    private static AppRoleDetail MakeRoleDetail(Guid publicId, long appId = 10L) => new(
        Id: 2L,
        PublicId: publicId,
        AppId: appId,
        Name: "Viewer",
        IsDefault: false,
        IsSystem: false,
        Permissions: []
    );

    private static Report MakeReport(Guid publicId, bool isDefault = false, string name = "Report") => new()
    {
        Id = 3L,
        PublicId = publicId,
        Name = name,
        ReportType = "Table",
        Visibility = "Personal",
        Definition = JsonSerializer.Serialize(new ReportDefinition()),
        IsDefault = isDefault,
        CreatedOn = DateTime.UtcNow,
    };
}

