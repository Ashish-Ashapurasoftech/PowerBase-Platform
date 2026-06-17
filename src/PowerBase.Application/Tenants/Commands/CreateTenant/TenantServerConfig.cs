namespace PowerBase.Application.Tenants.Commands.CreateTenant;

public record TenantServerConfig(
    string Host,
    int Port,
    string AdminLogin,
    string AdminPassword,
    bool Encrypt);
