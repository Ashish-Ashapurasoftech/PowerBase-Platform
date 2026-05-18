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
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private CreateAppCommandHandler CreateSut() => new(_appRepo, _queryContext);

    public CreateAppCommandHandlerTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
    }

    [Fact]
    public async Task HandleAsync_ValidCommand_ReturnsAppWithPublicId()
    {
        var expected = Guid.NewGuid();
        _appRepo.NameExistsAsync("My App").Returns(false);
        _appRepo.CreateAsync(Arg.Any<App>()).Returns(expected);
        var sut = CreateSut();

        var result = await sut.HandleAsync(new CreateAppCommand("My App", null, null, null));

        result.PublicId.Should().Be(expected);
        result.Name.Should().Be("My App");
        result.Status.Should().Be("Active");
    }

    [Fact]
    public async Task HandleAsync_DuplicateName_ThrowsDuplicateException()
    {
        _appRepo.NameExistsAsync("Taken").Returns(true);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new CreateAppCommand("Taken", null, null, null)))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Fact]
    public async Task HandleAsync_EmptyName_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new CreateAppCommand("", null, null, null)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_NameTooLong_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new CreateAppCommand(new string('x', 201), null, null, null)))
            .Should().ThrowAsync<ValidationException>();
    }
}
