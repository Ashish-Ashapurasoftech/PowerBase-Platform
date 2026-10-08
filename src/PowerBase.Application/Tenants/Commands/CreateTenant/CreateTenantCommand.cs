namespace PowerBase.Application.Tenants.Commands.CreateTenant;

public record CreateTenantCommand(
    string Name,
    TenantServerConfig? ServerConfig = null,
    string? ElasticPoolName = null,
    string? ServiceObjective = null);

