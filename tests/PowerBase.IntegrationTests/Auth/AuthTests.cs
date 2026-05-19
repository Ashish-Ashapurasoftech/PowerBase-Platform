using System.Net;
using FluentAssertions;
using PowerBase.IntegrationTests.Infrastructure;

namespace PowerBase.IntegrationTests.Auth;

[Collection("PowerBase")]
public class AuthTests : IntegrationTestBase
{
    public AuthTests(PowerBaseWebApplicationFactory factory) : base(factory) { }

    // ── Signup ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Signup_ValidRequest_Returns201WithIdentityToken()
    {
        var email = $"signup-{Guid.NewGuid():N}@example.com";

        var response = await PostAsync("/auth/signup", new
        {
            email,
            password = "Password123!",
            name = "Jane Doe",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var identity = await ReadData<IdentityDto>(response);
        identity.IdentityToken.Should().NotBeNullOrEmpty();
        identity.User.Email.Should().Be(email);
    }

    [Fact]
    public async Task Signup_DuplicateEmail_Returns409()
    {
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await PostAsync("/auth/signup", new { email, password = "Password123!", name = "A" });

        var response = await PostAsync("/auth/signup", new { email, password = "Password123!", name = "B" });

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
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Create Tenant (new-user path) ────────────────────────────────────────

    [Fact]
    public async Task CreateTenant_ValidRequest_Returns201WithScopedToken()
    {
        var signupRes = await PostAsync("/auth/signup", new
        {
            email = $"ct-{Guid.NewGuid():N}@example.com",
            password = "Password123!",
            name = "Tenant Owner",
        });
        var identity = await ReadData<IdentityDto>(signupRes);

        var response = await PostAsync("/tenants", new { name = "Acme Corp" }, identity.IdentityToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = await ReadData<AuthDto>(response);
        auth.Token.Should().NotBeNullOrEmpty();
        auth.TenantPublicId.Should().NotBeEmpty();
        auth.TenantName.Should().Be("Acme Corp");
    }

    // ── Login ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_ValidCredentials_Returns200WithIdentityTokenAndTenants()
    {
        var email = $"login-{Guid.NewGuid():N}@example.com";
        const string password = "Password123!";
        var signupRes = await PostAsync("/auth/signup", new { email, password, name = "Test" });
        var identity = await ReadData<IdentityDto>(signupRes);
        await PostAsync("/tenants", new { name = "LoginTestTenant" }, identity.IdentityToken);

        var response = await PostAsync("/auth/login", new { email, password });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginIdentity = await ReadData<IdentityWithTenantsDto>(response);
        loginIdentity.IdentityToken.Should().NotBeNullOrEmpty();
        loginIdentity.Tenants.Should().HaveCount(1);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var email = $"wrongpw-{Guid.NewGuid():N}@example.com";
        await PostAsync("/auth/signup", new { email, password = "Password123!", name = "Test" });

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

    // ── Select Tenant (returning-user path) ──────────────────────────────────

    [Fact]
    public async Task SelectTenant_ValidTenant_Returns200WithScopedToken()
    {
        var email = $"sel-{Guid.NewGuid():N}@example.com";
        const string password = "Password123!";
        var signupRes = await PostAsync("/auth/signup", new { email, password, name = "Test" });
        var signupIdentity = await ReadData<IdentityDto>(signupRes);
        var tenantRes = await PostAsync("/tenants", new { name = "SelectTenantTest" }, signupIdentity.IdentityToken);
        var tenantAuth = await ReadData<AuthDto>(tenantRes);

        var loginRes = await PostAsync("/auth/login", new { email, password });
        var loginIdentity = await ReadData<IdentityWithTenantsDto>(loginRes);

        var response = await PostAsync("/auth/select-tenant",
            new { tenantPublicId = tenantAuth.TenantPublicId },
            loginIdentity.IdentityToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var auth = await ReadData<AuthDto>(response);
        auth.Token.Should().NotBeNullOrEmpty();
        auth.TenantPublicId.Should().Be(tenantAuth.TenantPublicId);
    }

    // ── GetMe ────────────────────────────────────────────────────────────────

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

    private record IdentityDto(string IdentityToken, string ExpiresAt, UserDto User);
    private record IdentityWithTenantsDto(string IdentityToken, string ExpiresAt, UserDto User, List<TenantItem> Tenants);
    private record AuthDto(string Token, string ExpiresAt, UserDto User, Guid TenantPublicId, string TenantName);
    private record UserDto(Guid PublicId, string Email, string Name);
    private record TenantItem(Guid PublicId, string Name);
}
