using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.CreateApp;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Apps;

public class CreateAppCommandHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly ISchemaEngineService _schemaEngine = Substitute.For<ISchemaEngineService>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IFieldTypeRepository _fieldTypeRepo = Substitute.For<IFieldTypeRepository>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IAppRolePermissionRepository _permRepo = Substitute.For<IAppRolePermissionRepository>();
    private readonly PowerBase.Application.Fields.Commands.BulkCreateFields.BulkCreateFieldsCommandHandler _bulkCreateHandler = Substitute.For<PowerBase.Application.Fields.Commands.BulkCreateFields.BulkCreateFieldsCommandHandler>(Substitute.For<PowerBase.Application.Common.Interfaces.IAppTableRepository>(), Substitute.For<PowerBase.Application.Common.Interfaces.IAppFieldRepository>(), Substitute.For<PowerBase.Application.Common.Interfaces.IFieldTypeRepository>(), Substitute.For<PowerBase.Application.Fields.Commands.CreateField.CreateFieldCommandHandler>(Substitute.For<PowerBase.Application.Common.Interfaces.IAppTableRepository>(), Substitute.For<PowerBase.Application.Common.Interfaces.IAppFieldRepository>(), Substitute.For<PowerBase.Application.Common.Interfaces.IFieldTypeRepository>(), Substitute.For<PowerBase.Application.Common.Interfaces.ISchemaEngineService>(), Substitute.For<PowerBase.Application.Common.Interfaces.IQueryContext>(), Substitute.For<PowerBase.Application.Common.Interfaces.IAuditRepository>(), Substitute.For<PowerBase.Application.Common.Interfaces.IFormRepository>(), new PowerBase.Application.Fields.Settings.FieldSettingsValidatorRegistry(System.Array.Empty<PowerBase.Application.Fields.Settings.IFieldSettingsValidator>())));

    private CreateAppCommandHandler CreateSut() => new(_appRepo, _appRoleRepo, _appUserRepo, _uow, _queryContext, _tableRepo, _schemaEngine, _auditRepo, _fieldRepo, _reportRepo, _fieldTypeRepo, _formRepo, _userRepo, _permRepo, _bulkCreateHandler);

    public CreateAppCommandHandlerTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
        _userRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, PublicId = Guid.NewGuid(), Name = "Test User", Email = "test@example.com" });
    }

    private void SetupHappyPath(Guid publicId, long appId = 10)
    {
        _appRepo.NameExistsAsync(Arg.Any<string>()).Returns(false);
        _appRepo.CreateAsync(Arg.Any<App>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns((publicId, appId));
        _appRoleRepo.CreateAsync(Arg.Any<AppRole>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns((1L, Guid.NewGuid()));
        _tableRepo.NameExistsInAppAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _tableRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<CancellationToken>())
            .Returns((1L, Guid.NewGuid()));
    }

    [Fact]
    public async Task HandleAsync_ValidCommand_ReturnsAppWithPublicId()
    {
        var expected = Guid.NewGuid();
        SetupHappyPath(expected);
        var sut = CreateSut();

        var result = await sut.HandleAsync(new CreateAppCommand("My App", null, null, null, new[] { new TableSpec("Table 1") }));

        result.PublicId.Should().Be(expected);
        result.Name.Should().Be("My App");
        result.Status.Should().Be("Active");
    }

    [Fact]
    public async Task HandleAsync_ValidCommand_SeedsThreeBuiltInRoles()
    {
        SetupHappyPath(Guid.NewGuid());
        var sut = CreateSut();

        await sut.HandleAsync(new CreateAppCommand("My App", null, null, null, new[] { new TableSpec("Table 1") }));

        await _appRoleRepo.Received(3).CreateAsync(Arg.Any<AppRole>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
        await _appUserRepo.Received(1).CreateAsync(Arg.Any<AppUser>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DuplicateName_ThrowsDuplicateException()
    {
        _appRepo.NameExistsAsync("Taken").Returns(true);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new CreateAppCommand("Taken", null, null, null, Array.Empty<TableSpec>())))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Fact]
    public async Task HandleAsync_EmptyName_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new CreateAppCommand("", null, null, null, Array.Empty<TableSpec>())))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_NameTooLong_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new CreateAppCommand(new string('x', 201), null, null, null, Array.Empty<TableSpec>())))
            .Should().ThrowAsync<ValidationException>();
    }
}
