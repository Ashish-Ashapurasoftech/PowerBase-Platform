using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.UpdateAppVariable;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Apps;

/// <summary>A variable's name is fixed when it is created; only its value and description can change afterwards.</summary>
public class AppVariableNameLockTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppVariableRepository _variableRepo = Substitute.For<IAppVariableRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private readonly Guid _appPublicId = Guid.NewGuid();
    private readonly Guid _variablePublicId = Guid.NewGuid();
    private const long AppId = 123;

    private void GivenVariableNamed(string name) =>
        _variableRepo.GetByPublicIdAsync(AppId, _variablePublicId, Arg.Any<CancellationToken>())
            .Returns(new AppVariable { Id = 5, PublicId = _variablePublicId, AppId = AppId, Name = name, Value = "0.18" });

    public AppVariableNameLockTests() =>
        _appRepo.GetIdByPublicIdAsync(_appPublicId, Arg.Any<CancellationToken>()).Returns(AppId);

    private UpdateAppVariableCommandHandler Sut() => new(_appRepo, _variableRepo, _appAccessService);

    private UpdateAppVariableCommand Update(string name, string value = "0.20", string? description = "new note") =>
        new(_appPublicId, _variablePublicId, name, value, description);

    [Fact]
    public async Task The_value_and_description_can_be_changed_with_the_same_name()
    {
        GivenVariableNamed("Tax");

        await Sut().HandleAsync(Update("Tax"));

        await _variableRepo.Received(1).UpdateAsync(AppId, _variablePublicId, "Tax", "0.20", "new note", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("GST")]
    [InlineData("tax")]   // even a change of letter case
    [InlineData("Tax ")]
    public async Task A_different_name_is_rejected_and_nothing_is_saved(string newName)
    {
        GivenVariableNamed("Tax");

        var act = () => Sut().HandleAsync(Update(newName));

        var error = await act.Should().ThrowAsync<ValidationException>();
        error.Which.Errors["Name"].Should().ContainSingle().Which.Should().Contain("can't be changed");
        await _variableRepo.DidNotReceive().UpdateAsync(
            Arg.Any<long>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_variable_made_before_the_name_rules_can_still_have_its_value_changed()
    {
        // Created when any name was allowed — its name has brackets, which a new variable's name may not.
        GivenVariableNamed("Tax [old]");

        await Sut().HandleAsync(Update("Tax [old]"));

        await _variableRepo.Received(1).UpdateAsync(AppId, _variablePublicId, "Tax [old]", "0.20", "new note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_name_that_is_saved_is_always_the_one_the_variable_already_has()
    {
        GivenVariableNamed("Tax");

        await Sut().HandleAsync(Update("Tax"));

        await _variableRepo.Received().UpdateAsync(AppId, _variablePublicId, "Tax", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _variableRepo.DidNotReceive().NameExistsAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
