using System.Collections.Generic;
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

        var identityClaims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, jwtId.ToString()),
        };
        if (!string.IsNullOrEmpty(user.SystemRoleCode))
            identityClaims.Add(new Claim("role", user.SystemRoleCode));
        var claims = identityClaims.ToArray();

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

        var claimList = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,   user.Id.ToString()),
            new("tid",                          tenantId.ToString()),
            new(JwtRegisteredClaimNames.Jti,   jwtId.ToString()),
            new(JwtRegisteredClaimNames.Name,  user.Name ?? string.Empty),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
        };
        if (!string.IsNullOrEmpty(user.SystemRoleCode))
            claimList.Add(new Claim("role", user.SystemRoleCode));
        var claims = claimList.ToArray();

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public bool ValidateToken(string token, out long userId, out long tenantId, out Guid jwtId, out string userName, out string userEmail, out string systemRoleCode)
    {
        userId = 0; tenantId = 0; jwtId = Guid.Empty; userName = string.Empty; userEmail = string.Empty; systemRoleCode = string.Empty;
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

            userId    = long.Parse(principal.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);
            tenantId  = long.TryParse(principal.FindFirst("tid")?.Value, out var tid) ? tid : 0;
            jwtId     = Guid.Parse(principal.FindFirst(JwtRegisteredClaimNames.Jti)!.Value);
            userName       = principal.FindFirst(JwtRegisteredClaimNames.Name)?.Value ?? string.Empty;
            userEmail      = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value ?? string.Empty;
            systemRoleCode = principal.FindFirst("role")?.Value ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string GeneratePasswordResetToken(User user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(30);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("pr_sub", user.Id.ToString()),
            new("pr_hash", user.HashedPassword)
        }.ToArray();

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public bool ValidatePasswordResetToken(string token, out long userId, out string passwordHash)
    {
        userId = 0; passwordHash = string.Empty;
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

            userId = long.Parse(principal.FindFirst("pr_sub")!.Value);
            passwordHash = principal.FindFirst("pr_hash")!.Value;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
