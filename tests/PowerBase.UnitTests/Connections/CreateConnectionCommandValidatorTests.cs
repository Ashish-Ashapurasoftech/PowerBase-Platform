using System.Linq;
using FluentAssertions;
using PowerBase.Application.Connections.Commands.CreateConnection;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Connections;

public class CreateConnectionCommandValidatorTests
{
    private readonly CreateConnectionCommandValidator _validator = new();

    private static CreateConnectionCommand Valid(
        string authMode = PipelineAccountAuthModes.UserToken,
        string subdomain = "acme",
        string userToken = "pb_ut_abcdef1234567890",
        string? name = null)
        => new(authMode, subdomain, userToken, name);

    [Fact]
    public void Validate_UserTokenMode_Passes()
        => _validator.Validate(Valid()).IsValid.Should().BeTrue();

    [Fact]
    public void Validate_CurrentUserMode_ReturnsValidationError()
    {
        // 'Authenticate with my user' selects an existing permitted realm on the client and
        // must never create an account row.
        var result = _validator.Validate(Valid(authMode: PipelineAccountAuthModes.CurrentUser));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateConnectionCommand.AuthMode));
    }

    [Fact]
    public void Validate_EmptyAuthMode_ReturnsValidationError()
        => _validator.Validate(Valid(authMode: "")).IsValid.Should().BeFalse();

    [Fact]
    public void Validate_EmptySubdomain_ReturnsValidationError()
    {
        var result = _validator.Validate(Valid(subdomain: ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateConnectionCommand.Subdomain));
    }

    [Theory]
    [InlineData("acme corp")]
    [InlineData("-acme")]
    [InlineData("acme_corp")]
    [InlineData("acme.example.com")]
    [InlineData("https://acme")]
    public void Validate_MalformedSubdomain_ReturnsValidationError(string subdomain)
        => _validator.Validate(Valid(subdomain: subdomain)).IsValid.Should().BeFalse();

    [Theory]
    [InlineData("acme")]
    [InlineData("acme-corp")]
    [InlineData("acme2")]
    [InlineData("2acme")]
    public void Validate_WellFormedSubdomain_Passes(string subdomain)
        => _validator.Validate(Valid(subdomain: subdomain)).IsValid.Should().BeTrue();

    [Fact]
    public void Validate_SubdomainOver100Chars_ReturnsValidationError()
        => _validator.Validate(Valid(subdomain: new string('a', 101))).IsValid.Should().BeFalse();

    [Fact]
    public void Validate_EmptyUserToken_ReturnsValidationError()
    {
        var result = _validator.Validate(Valid(userToken: ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateConnectionCommand.UserToken));
    }

    [Fact]
    public void Validate_UserTokenOver512Chars_ReturnsValidationError()
        => _validator.Validate(Valid(userToken: new string('x', 513))).IsValid.Should().BeFalse();

    [Fact]
    public void Validate_NameOver200Chars_ReturnsValidationError()
        => _validator.Validate(Valid(name: new string('n', 201))).IsValid.Should().BeFalse();

    [Fact]
    public void Validate_NameOmitted_Passes()
        => _validator.Validate(Valid(name: null)).IsValid.Should().BeTrue();
}
