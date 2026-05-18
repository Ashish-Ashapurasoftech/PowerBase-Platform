using FluentAssertions;
using PowerBase.Application.Auth.Commands.Signup;

namespace PowerBase.UnitTests.Auth;

public class SignupCommandValidatorTests
{
    private readonly SignupCommandValidator _sut = new();

    private static SignupCommand Valid() => new("test@example.com", "password123", "Jane Doe");

    [Fact]
    public async Task Validate_ValidCommand_Passes()
    {
        var result = await _sut.ValidateAsync(Valid());
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Validate_InvalidEmail_Fails(string email)
    {
        var result = await _sut.ValidateAsync(Valid() with { Email = email });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email");
    }

    [Fact]
    public async Task Validate_EmailTooLong_Fails()
    {
        var longEmail = new string('a', 251) + "@x.com";
        var result = await _sut.ValidateAsync(Valid() with { Email = longEmail });
        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task Validate_InvalidPassword_Fails(string password)
    {
        var result = await _sut.ValidateAsync(Valid() with { Password = password });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Password");
    }

    [Fact]
    public async Task Validate_EmptyName_Fails()
    {
        var result = await _sut.ValidateAsync(Valid() with { Name = "" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
    }
}
