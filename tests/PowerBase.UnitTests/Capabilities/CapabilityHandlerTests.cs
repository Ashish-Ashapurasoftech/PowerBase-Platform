using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Capabilities.Commands.SaveRoleCapabilities;
using PowerBase.Application.Capabilities.Commands.UpdateRoleCapability;
using PowerBase.Application.Capabilities.Dtos;
using PowerBase.Application.Capabilities.Queries.GetRoleCapabilities;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Capabilities;

public class CapabilityHandlerTests
{
    private readonly ICapabilityRepository _capabilityRepo = Substitute.For<ICapabilityRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    [Fact]
    public async Task GetRoleCapabilities_ReturnsExpectedCapabilities()
    {
        // Arrange
        var roleId = Guid.NewGuid();
        var expected = new List<RoleCapabilityDto>
        {
            new() { Id = "schema", Name = "Schema Builder", IsEnabled = true, Status = "full" },
            new() { Id = "form", Name = "Form Builder", IsEnabled = false, Status = "none" }
        };

        _capabilityRepo.GetRoleCapabilitiesAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(expected);

        var handler = new GetRoleCapabilitiesQueryHandler(_capabilityRepo);

        // Act
        var result = await handler.HandleAsync(new GetRoleCapabilitiesQuery(roleId));

        // Assert
        result.Should().HaveCount(2);
        result[0].Id.Should().Be("schema");
        result[0].IsEnabled.Should().BeTrue();
        result[1].Id.Should().Be("form");
        result[1].IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SaveRoleCapabilities_WhenRoleNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var roleId = Guid.NewGuid();
        _appRoleRepo.GetByPublicIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns((AppRole?)null);

        var handler = new SaveRoleCapabilitiesCommandHandler(_capabilityRepo, _appRoleRepo, _auditRepo, _appAccessService);

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(new SaveRoleCapabilitiesCommand(roleId, new[] { "schema", "form" })));
    }

    [Fact]
    public async Task SaveRoleCapabilities_WhenValid_SavesAndAudits()
    {
        // Arrange
        var roleId = Guid.NewGuid();
        var role = new AppRole { Id = 10, PublicId = roleId, Name = "Custom Role", AppId = 1 };
        _appRoleRepo.GetByPublicIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(role);

        var handler = new SaveRoleCapabilitiesCommandHandler(_capabilityRepo, _appRoleRepo, _auditRepo, _appAccessService);

        // Act
        await handler.HandleAsync(new SaveRoleCapabilitiesCommand(roleId, new[] { "schema", "form" }));

        // Assert
        await _appAccessService.Received(1).RequirePermissionByAppIdAsync(role.AppId, Domain.Constants.PermissionCodes.RolesManage, Arg.Any<CancellationToken>());
        await _capabilityRepo.Received(1).SaveRoleCapabilitiesAsync(roleId, Arg.Is<IReadOnlyList<string>>(l => l.Contains("schema") && l.Contains("form")), Arg.Any<CancellationToken>());
        await _auditRepo.Received(1).LogActivityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateRoleCapability_WhenRoleNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var roleId = Guid.NewGuid();
        _appRoleRepo.GetByPublicIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns((AppRole?)null);

        var handler = new UpdateRoleCapabilityCommandHandler(_capabilityRepo, _appRoleRepo, _auditRepo, _appAccessService);

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(new UpdateRoleCapabilityCommand(roleId, "report", true)));
    }

    [Fact]
    public async Task UpdateRoleCapability_WhenValid_UpdatesAndAudits()
    {
        // Arrange
        var roleId = Guid.NewGuid();
        var role = new AppRole { Id = 10, PublicId = roleId, Name = "Custom Role", AppId = 1 };
        _appRoleRepo.GetByPublicIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(role);

        var handler = new UpdateRoleCapabilityCommandHandler(_capabilityRepo, _appRoleRepo, _auditRepo, _appAccessService);

        // Act
        await handler.HandleAsync(new UpdateRoleCapabilityCommand(roleId, "report", true));

        // Assert
        await _appAccessService.Received(1).RequirePermissionByAppIdAsync(role.AppId, Domain.Constants.PermissionCodes.RolesManage, Arg.Any<CancellationToken>());
        await _capabilityRepo.Received(1).UpdateRoleCapabilityAsync(roleId, "report", true, Arg.Any<CancellationToken>());
        await _auditRepo.Received(1).LogActivityAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
