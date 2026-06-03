using Microsoft.Extensions.Configuration;

namespace PowerBase.Infrastructure.Persistence;

public class ConfigSecretResolver : ISecretResolver
{
    private readonly string _defaultConnectionString;

    public ConfigSecretResolver(IConfiguration configuration)
    {
        _defaultConnectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string is not configured.");
    }

    public Task<string> ResolveAsync(string? serverRef, string? secretRef, CancellationToken ct = default)
        => Task.FromResult(_defaultConnectionString);
}
