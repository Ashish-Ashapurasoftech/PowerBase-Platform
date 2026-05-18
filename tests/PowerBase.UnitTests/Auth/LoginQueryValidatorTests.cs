using FluentAssertions;
using PowerBase.Application.Auth.Queries.Login;

namespace PowerBase.UnitTests.Auth;

public class LoginQueryValidatorTests
{
    private readonly LoginQueryValidator _sut = new();

    private static LoginQuery Valid() => new("user@example.com", "password123", "127.0.0.1");

    [Fact]
    public async Task Validate_ValidQuery_Passes()
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
    public async Task Validate_EmptyPassword_Fails()
    {
        var result = await _sut.ValidateAsync(Valid() with { Password = "" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Password");
    }
}
