using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IJwtService
{
    string GenerateIdentityToken(User user, out Guid jwtId, out DateTime expiresAt);
    string GenerateToken(User user, long tenantId, out Guid jwtId, out DateTime expiresAt);
    bool ValidateToken(string token, out long userId, out long tenantId, out Guid jwtId);
}
