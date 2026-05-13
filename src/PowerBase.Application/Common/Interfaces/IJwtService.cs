using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IJwtService
{
    string GenerateToken(User user, long tenantId, out Guid jwtId);
    bool ValidateToken(string token, out long userId, out long tenantId, out Guid jwtId);
}
