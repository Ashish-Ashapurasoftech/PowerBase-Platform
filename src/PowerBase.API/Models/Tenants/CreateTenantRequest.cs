namespace PowerBase.API.Models.Tenants;

public class CreateTenantRequest
{
    public string Name { get; set; } = string.Empty;
    public CreateTenantServerConfigRequest? ServerConfig { get; set; }
}

public class CreateTenantServerConfigRequest
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1433;
    public string AdminLogin { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public bool Encrypt { get; set; } = true;
}
