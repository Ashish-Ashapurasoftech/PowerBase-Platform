using Microsoft.AspNetCore.Mvc;
using PowerBase.API.ConnectedTables.Workers;
using PowerBase.Infrastructure.ConnectedTables;

namespace PowerBase.API.ConnectedTables;

public static class ConnectedTablesApiExtensions
{
    /// <summary>Everything the Connected Tables module needs in the API host: its services, the read-only write filter
    /// (applies to connected tables only) and its two background workers.</summary>
    public static IServiceCollection AddConnectedTablesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddConnectedTables(configuration);
        services.AddScoped<ConnectedTableWriteFilter>();
        services.Configure<MvcOptions>(o => o.Filters.AddService<ConnectedTableWriteFilter>());
        services.AddHostedService<ConnectedSchedulerWorker>();
        services.AddHostedService<ConnectedSyncWorker>();
        return services;
    }
}
