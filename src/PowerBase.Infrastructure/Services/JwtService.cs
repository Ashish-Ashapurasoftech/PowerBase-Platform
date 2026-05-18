using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Infrastructure.Services;

public class JwtService : IJwtService
{
    private readonly string _secretKey;
    private readonly int _expiresInMinutes;
    private readonly string _issuer;
    private readonly string _audience;

    private readonly int _identityTokenExpiresInMinutes;

    public JwtService(IConfiguration configuration)
    {
        _secretKey = configuration["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey is not configured.");
        _expiresInMinutes = int.Parse(configuration["Jwt:ExpiresInMinutes"]
            ?? throw new InvalidOperationException("Jwt:ExpiresInMinutes is not configured."));
        _identityTokenExpiresInMinutes = int.TryParse(configuration["Jwt:IdentityTokenExpiresInMinutes"], out var idExp)
            ? idExp : 15;
        _issuer = configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException("Jwt:Issuer is not configured.");
        _audience = configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException("Jwt:Audience is not configured.");
    }

    public string GenerateIdentityToken(User user, out Guid jwtId, out DateTime expiresAt)
    {
        jwtId = Guid.NewGuid();
        expiresAt = DateTime.UtcNow.AddMinutes(_identityTokenExpiresInMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, jwtId.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateToken(User user, long tenantId, out Guid jwtId, out DateTime expiresAt)
    {
        jwtId = Guid.NewGuid();
        expiresAt = DateTime.UtcNow.AddMinutes(_expiresInMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("tid", tenantId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, jwtId.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public bool ValidateToken(string token, out long userId, out long tenantId, out Guid jwtId)
    {
        userId = 0; tenantId = 0; jwtId = Guid.Empty;
        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));
            var handler = new JwtSecurityTokenHandler
            {
                MapInboundClaims = false
            };
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            }, out _);

            userId = long.Parse(principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);
            tenantId = long.TryParse(principal.FindFirst("tid")?.Value, out var tid) ? tid : 0;
            jwtId = Guid.Parse(principal.FindFirst(JwtRegisteredClaimNames.Jti)!.Value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
