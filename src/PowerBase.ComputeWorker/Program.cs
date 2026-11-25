using PowerBase.Application.Common.Interfaces;
using PowerBase.ComputeWorker;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;

// PowerBase.ComputeWorker: fills stored c_{fid} columns (Lookup and Summary) from meta.ComputeOutbox, builds switched-on fields and checks
// them nightly. It runs with no user: only the tenant id is ever set in the query context.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMemoryCache();

// Tenant database routing: the same classes the API uses, so both reach the same databases the same way.
builder.Services.AddSingleton<IControlConnectionFactory, ControlConnectionFactory>();
if (!string.IsNullOrEmpty(builder.Configuration["KeyVault:Uri"]))
    builder.Services.AddSingleton<ISecretResolver, KeyVaultSecretResolver>();
else
    builder.Services.AddSingleton<ISecretResolver, ConfigSecretResolver>();
builder.Services.AddSingleton<ITenantConnectionResolver, TenantConnectionResolver>();
builder.Services.AddSingleton<IReplicaLagMonitor, ReplicaLagMonitor>();
builder.Services.AddScoped<ITenantConnectionFactory, TenantConnectionFactory>();
builder.Services.AddScoped<QueryContext>();
builder.Services.AddScoped<IQueryContext>(sp => sp.GetRequiredService<QueryContext>());

// Compute
// Every tenant query of the worker (the read code's summaries and lookups included) gets a long command timeout: a summary over a 1.1 M-row
// child table ran past the 30 s default and Failed whole builds on UAT. Compute:CommandTimeoutSeconds, default 600; 0 = SqlClient default.
var commandTimeout = builder.Configuration.GetValue("Compute:CommandTimeoutSeconds", 600);
if (commandTimeout > 0) PowerBase.Infrastructure.Persistence.TenantConnectionFactory.ProcessCommandTimeoutSeconds = commandTimeout;
builder.Services.AddSingleton<IComputeSettings, ComputeSettings>();
builder.Services.AddScoped<IComputeOutbox, ComputeOutboxRepository>();
builder.Services.AddSingleton<IComputeQueuePublisher, ServiceBusComputePublisher>();
builder.Services.AddSingleton<ComputeTenantDirectory>();
builder.Services.AddSingleton<ComputeWorkerCounters>();
builder.Services.AddSingleton<ComputeModelCache>();
// All DDL (the c_ columns) goes through the schema engine, as everywhere else.
builder.Services.AddScoped<ISchemaEngineService, SchemaEngineService>();
// Encrypted apps: what a stored field reads is decrypted with the API's own keys and cipher (Encryption:* settings, the same as the API's),
// and the computed value is stored plain in c_.
builder.Services.AddSingleton<AesEncryptionService>();
builder.Services.AddSingleton<IEncryptionService>(sp =>
    new CachedDekEncryptionService(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<AesEncryptionService>()));
builder.Services.AddScoped<ComputeDecryptor>();

// The API's read code, for fields worked out the way a report works them out (ProjectedPlan): chains of calculated fields, criteria on
// calculated or parent fields, Today() / relative dates, URLRoot(), Combined Text. The metadata cache is OFF here: the worker must see a
// field definition change at once, and the API's cache invalidation does not reach this process.
builder.Services.Configure<PowerBase.Application.Common.Configurations.MetadataCacheOptions>(o => o.Enabled = false);
builder.Services.AddSingleton<IMetadataCache, MetadataCache>();
builder.Services.AddSingleton<PowerBase.Formula.FormulaEngine>();
builder.Services.AddSingleton<IFormulaRuntimeContext, WorkerFormulaRuntimeContext>();
builder.Services.AddScoped<IAppRepository, PowerBase.Infrastructure.Repositories.AppRepository>();
builder.Services.AddScoped<IAppVariableRepository, PowerBase.Infrastructure.Repositories.AppVariableRepository>();
builder.Services.AddScoped<IAppRoleRepository, PowerBase.Infrastructure.Repositories.AppRoleRepository>();
builder.Services.AddScoped<IAppUserRepository, PowerBase.Infrastructure.Repositories.AppUserRepository>();
builder.Services.AddScoped<IAppTableRepository, PowerBase.Infrastructure.Repositories.AppTableRepository>();
builder.Services.AddScoped<IUserRepository, PowerBase.Infrastructure.Repositories.UserRepository>();
builder.Services.AddScoped<IAppFieldRepository, PowerBase.Infrastructure.Repositories.AppFieldRepository>();
builder.Services.AddScoped<IRecordRepository, PowerBase.Infrastructure.Repositories.RecordRepository>();
builder.Services.AddScoped<IRelationshipRepository, PowerBase.Infrastructure.Repositories.RelationshipRepository>();
builder.Services.AddScoped<PowerBase.Application.Compute.IMaterializedFieldState, MaterializedFieldStateRepository>();
builder.Services.AddScoped<PowerBase.Application.Formulas.IFormulaProjector, PowerBase.Application.Formulas.FormulaProjector>();
builder.Services.AddScoped<PowerBase.Application.Relationships.IRelationalProjector, PowerBase.Application.Relationships.RelationalProjector>();
builder.Services.AddScoped<ProjectedValueComputer>();

builder.Services.AddScoped<ComputeTableProcessor>();
builder.Services.AddScoped<MaterializeBuilder>();
builder.Services.AddScoped<MaterializedIndexMaintainer>();

// Search index (decision Q7): stored values of Searchable fields are pushed to the index after they change. Publishing is skipped while
// AzureServiceBus is not configured; the outbox rows still record what is owed.
builder.Services.AddSingleton<IMessagePublisher, ServiceBusPublisher>();
builder.Services.AddScoped<ISearchOutbox, PowerBase.Infrastructure.Repositories.SearchOutboxRepository>();
builder.Services.AddScoped<ISearchFieldJobs, PowerBase.Infrastructure.Repositories.SearchFieldJobRepository>();
builder.Services.AddScoped<ComputeSearchNotifier>();

builder.Services.AddHostedService<ComputeQueueWorker>();
builder.Services.AddHostedService<ComputeOutboxRelay>();
builder.Services.AddHostedService<ReplicaHeartbeatWriter>();
builder.Services.AddHostedService<BackfillRunner>();
builder.Services.AddHostedService<ReconciliationWorker>();
builder.Services.AddHostedService<DailyRefreshRunner>();

builder.Build().Run();
