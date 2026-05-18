using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Auth;

[Collection("PowerBase")]
public class AuthTests : IntegrationTestBase
{
    public AuthTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Signup_ValidRequest_Returns201WithToken()
    {
        var email = $"signup-{Guid.NewGuid():N}@example.com";

        var response = await PostAsync("/auth/signup", new
        {
            email,
            password = "Password123!",
            name = "Jane Doe",
            tenantName = "Acme Corp",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = await ReadData<AuthResponseDto>(response);
        auth.Token.Should().NotBeNullOrEmpty();
        auth.User.Email.Should().Be(email);
    }

    [Fact]
    public async Task Signup_DuplicateEmail_Returns409()
    {
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await PostAsync("/auth/signup", new { email, password = "Password123!", name = "A", tenantName = "T1" });

        var response = await PostAsync("/auth/signup", new { email, password = "Password123!", name = "B", tenantName = "T2" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Signup_InvalidEmail_Returns400()
    {
        var response = await PostAsync("/auth/signup", new
        {
            email = "not-an-email",
            password = "Password123!",
            name = "Test",
            tenantName = "Tenant",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Signup_ShortPassword_Returns400()
    {
        var response = await PostAsync("/auth/signup", new
        {
            email = $"pw-{Guid.NewGuid():N}@example.com",
            password = "short",
            name = "Test",
            tenantName = "Tenant",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200WithToken()
    {
        var email = $"login-{Guid.NewGuid():N}@example.com";
        const string password = "Password123!";
        await PostAsync("/auth/signup", new { email, password, name = "Test", tenantName = "TestTenant" });

        var response = await PostAsync("/auth/login", new { email, password });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var auth = await ReadData<AuthResponseDto>(response);
        auth.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var email = $"wrongpw-{Guid.NewGuid():N}@example.com";
        await PostAsync("/auth/signup", new { email, password = "Password123!", name = "Test", tenantName = "T" });

        var response = await PostAsync("/auth/login", new { email, password = "WrongPassword999!" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_UnknownEmail_Returns401()
    {
        var response = await PostAsync("/auth/login", new
        {
            email = $"nobody-{Guid.NewGuid():N}@example.com",
            password = "Password123!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMe_WithValidToken_ReturnsUserProfile()
    {
        var (token, email) = await SignupAsync();

        var response = await GetAsync("/auth/me", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await ReadData<UserDto>(response);
        user.Email.Should().Be(email);
        user.PublicId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetMe_WithoutToken_Returns401()
    {
        var response = await GetAsync("/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private record AuthResponseDto(string Token, string ExpiresAt, UserDto User);
    private record UserDto(Guid PublicId, string Email, string Name);
}
