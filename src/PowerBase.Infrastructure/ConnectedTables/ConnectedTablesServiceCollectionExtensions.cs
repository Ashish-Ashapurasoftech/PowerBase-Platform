using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.ConnectedTables;
using PowerBase.Application.ConnectedTables.Access;
using PowerBase.Application.ConnectedTables.Handlers;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Sync;

namespace PowerBase.Infrastructure.ConnectedTables;

/// <summary>Registers the whole Connected Tables module (application services and their Dapper implementations).
/// The only line the host needs is <c>services.AddConnectedTables(configuration)</c>.</summary>
public static class ConnectedTablesServiceCollectionExtensions
{
    public static IServiceCollection AddConnectedTables(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ConnectedTablesOptions>(configuration.GetSection(ConnectedTablesOptions.SectionName));

        services.AddScoped<IConnectedConnectionRepository, ConnectedConnectionRepository>();
        services.AddScoped<IConnectedTableRepository, ConnectedTableRepository>();
        services.AddScoped<IConnectedRunRepository, ConnectedRunRepository>();
        services.AddScoped<IConnectedDataWriter, ConnectedDataWriter>();
        services.AddScoped<IConnectedControlRepository, ConnectedControlRepository>();
        services.AddScoped<IConnectedRunQueue, ConnectedRunQueue>();
        services.AddScoped<IConnectedAppTokenReader, ConnectedAppTokenReader>();
        services.AddScoped<IConnectedTableLock, ConnectedTableLock>();

        services.AddSingleton<ConnectedSourceScopeFactory>();
        services.AddScoped<ConnectedAccessVerifier>();
        services.AddScoped<ConnectedPermissions>();
        services.AddScoped<ConnectedRunStarter>();
        services.AddScoped<ConnectedNotifier>();
        services.AddScoped<ConnectedSyncProcessor>();

        services.AddScoped<ConnectionHandlers>();
        services.AddScoped<ConnectedTableHandlers>();
        services.AddScoped<ConnectExistingTableHandler>();
        services.AddScoped<ConnectedRunHandlers>();
        services.AddScoped<DataSharingHandlers>();
        return services;
    }
}
