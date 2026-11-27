using PowerBase.API.Middleware;
using PowerBase.Application.Groups.Commands.CreateGroup;
using PowerBase.Application.Groups.Commands.DeleteGroup;
using PowerBase.Application.Groups.Commands.UpdateGroup;
using PowerBase.Application.Groups.Queries.GetGroup;
using PowerBase.Application.Groups.Queries.ListGroups;
using PowerBase.Application.Groups.Commands.AddGroupMembers;
using PowerBase.Application.Groups.Commands.RemoveGroupMember;
using PowerBase.Application.Groups.Queries.ListGroupMembers;
using PowerBase.Application.Groups.Queries.GetUserEffectivePermissions;
using PowerBase.Application.Apps.Commands.AddAppUser;
using PowerBase.Application.Apps.Commands.ChangeAppUserRole;
using PowerBase.Application.Apps.Commands.CreateApp;
using PowerBase.Application.Apps.Commands.CreateAppRole;
using PowerBase.Application.Apps.Commands.DeleteApp;
using PowerBase.Application.Apps.Commands.DeleteAppRole;
using PowerBase.Application.Apps.Commands.UpdateAppRole;
using PowerBase.Application.Apps.Commands.UpdateApp;
using PowerBase.Application.Apps.Commands.RemoveAppUser;
using PowerBase.Application.Apps.Commands.InviteAppUser;
using PowerBase.Application.Apps.Commands.UpdateUserPickerVisibility;
using PowerBase.Application.Apps.Commands.CreateAppVariable;
using PowerBase.Application.Apps.Commands.UpdateAppVariable;
using PowerBase.Application.Apps.Commands.DeleteAppVariable;
using PowerBase.Application.Apps.Queries.ListAppVariables;
using PowerBase.Application.Apps.Queries.GetApp;
using PowerBase.Application.Apps.Queries.ListAppRoles;
using PowerBase.Application.Apps.Queries.ListApps;
using PowerBase.Application.Apps.Queries.ListAppUsers;
using PowerBase.Application.Auth.Commands.AcceptInvite;
using PowerBase.Application.Auth.Commands.ResetPassword;
using PowerBase.Application.Auth.Commands.ForgotPassword;
using PowerBase.Application.Auth.Commands.SelectTenant;
using PowerBase.Application.Auth.Commands.RefreshToken;
using PowerBase.Application.Auth.Commands.Signup;
using PowerBase.Application.Auth.Commands.UpdateMyPreferences;
using PowerBase.Application.Auth.Commands.UpdateUserProfile;
using PowerBase.Application.Auth.Queries.GetMe;
using PowerBase.Application.Auth.Queries.GetMyPreferences;
using PowerBase.Application.Auth.Queries.Login;
using PowerBase.Application.Apps.Queries.GetAppBranding;
using PowerBase.Application.Apps.Commands.UpdateAppBranding;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Services;
using PowerBase.Application.Tenants.Commands.CreateTenant;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Commands.DeleteField;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Commands.RestoreFieldVersion;
using PowerBase.Application.Fields.Common;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Application.Fields.Queries.ListFields;
using PowerBase.Application.Fields.Queries.ListFieldVersions;
using PowerBase.Application.Fields.Queries.GetFieldVersionDetail;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Records.Commands.CreateRecord;
using PowerBase.Application.Records.Commands.DeleteRecord;
using PowerBase.Application.Records.Commands.MassUpdateRecords;
using PowerBase.Application.Records.Commands.UpdateRecord;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Application.Records.Queries.ListRecords;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Commands.DeleteReport;
using PowerBase.Application.Reports.Commands.SetDefaultReport;
using PowerBase.Application.Reports.Commands.UpdateDefaultReportSettings;
using PowerBase.Application.Reports.Commands.UpdateReport;
using PowerBase.Application.Reports.Queries.GetDefaultReportSettings;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Application.Reports.Queries.ListReports;
using PowerBase.Application.Reports.Queries.ListReportsByTable;
using PowerBase.Application.Reports.Queries.ExportReport;
using PowerBase.Application.Reports.Queries.ResolveDefaultReport;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Application.Reports.Validation;
using PowerBase.Application.Roles.Commands.CreateRole;
using PowerBase.Application.Roles.Commands.DeleteRole;
using PowerBase.Application.Roles.Commands.UpdateRole;
using PowerBase.Application.Roles.Commands.UpdateRolePermissions;
using PowerBase.Application.Roles.Queries.GetRolePermissions;
using PowerBase.Application.Roles.Queries.ListPermissions;
using PowerBase.Application.Roles.Queries.ListRoles;
using PowerBase.Application.Tables.Commands.BulkDeleteTables;
using PowerBase.Application.Tables.Commands.SetTablesShowInBar;
using PowerBase.Application.Tables.Commands.CreateTable;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Application.Tables.Commands.UpdateTable;
using PowerBase.Application.Tables.Queries.GetTable;
using PowerBase.Application.Tables.Queries.ListTables;
using PowerBase.Application.Tables.Queries.ListTableNavItems;
using PowerBase.Application.AuditLogs.Queries.ExportAuditLogsCsv;
using PowerBase.Application.AuditLogs.Queries.ListAuditLogs;
using PowerBase.Application.Forms.Commands.CreateForm;
using PowerBase.Application.Forms.Commands.UpdateFormSettings;
using PowerBase.Application.Forms.Commands.SaveFormLayout;
using PowerBase.Application.Forms.Commands.DeleteForm;
using PowerBase.Application.Forms.Commands.DuplicateForm;
using PowerBase.Application.Forms.Commands.CreateFormRule;
using PowerBase.Application.Forms.Commands.SaveFormRule;
using PowerBase.Application.Forms.Commands.DeleteFormRule;
using PowerBase.Application.Forms.Commands.DuplicateFormRule;
using PowerBase.Application.Forms.Commands.ReorderFormRules;
using PowerBase.Application.Forms.Commands.ToggleFormRule;
using PowerBase.Application.Forms.Queries.GetForm;
using PowerBase.Application.Forms.Queries.ListForms;
using PowerBase.Application.Forms.Queries.GetFormLayout;
using PowerBase.Application.Forms.Queries.ListFormRules;
using PowerBase.Application.Forms.Queries.GetFormRule;
using PowerBase.Application.Users.Commands.ChangeUserRole;
using PowerBase.Application.Users.Commands.InviteUser;
using PowerBase.Application.Users.Commands.RemoveUser;
using PowerBase.Application.Users.Queries.ListUsers;
using PowerBase.Application.Fields.Settings;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Provisioning;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.Services;
using PowerBase.Infrastructure.UOW;
using Serilog;
using Azure.Monitor.OpenTelemetry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) =>
{
    // Console comes from configuration ("Serilog:WriteTo"). Application Insights is added only when a connection
    // string is supplied (setting "ApplicationInsights:ConnectionString" or the standard APPLICATIONINSIGHTS_CONNECTION_STRING),
    // so worker errors, dead-letters and indexing failures reach a place someone can query.
    cfg.ReadFrom.Configuration(ctx.Configuration);
    var appInsightsConnection = ctx.Configuration["ApplicationInsights:ConnectionString"]
        ?? ctx.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    if (!string.IsNullOrWhiteSpace(appInsightsConnection) && !appInsightsConnection.StartsWith("<"))
        cfg.WriteTo.ApplicationInsights(appInsightsConnection, new Serilog.Sinks.ApplicationInsights.TelemetryConverters.TraceTelemetryConverter());
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];


builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins);
        }
        else
        {
            policy.SetIsOriginAllowed(origin =>
                new Uri(origin).Host.EndsWith(".azurestaticapps.net") ||
                new Uri(origin).Host == "localhost");
        }
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              // Every API call carries an Authorization header, so the browser sends an OPTIONS preflight first. Without a
              // max age it re-asks within seconds, doubling the round trips; 2 hours is the most Chromium honours.
              .SetPreflightMaxAge(TimeSpan.FromHours(2));
    });
});

// Request, SQL and HTTP dependency, and Live Metrics telemetry for Application Insights. App Service supplies
// APPLICATIONINSIGHTS_CONNECTION_STRING; without one (local runs) nothing is added. The App Service codeless agent does not
// instrument this .NET 8 app on Linux, so the app does it itself.
var azureMonitorConnection = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? builder.Configuration["ApplicationInsights:ConnectionString"];
if (!string.IsNullOrWhiteSpace(azureMonitorConnection) && !azureMonitorConnection.StartsWith("<"))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor(o =>
    {
        o.ConnectionString = azureMonitorConnection;
        // The background workers poll SQL every few seconds; sampling keeps that volume, and the ingestion bill, down.
        o.SamplingRatio = builder.Configuration.GetValue("ApplicationInsights:SamplingRatio", 0.25f);
    })
    // Cache hit/miss/bytes and report repeat counts (doc 34 §4): customMetrics in Application Insights.
    .WithMetrics(m => m.AddMeter(PowerBase.Application.Common.Caching.CacheMetrics.MeterName));
}


builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "PowerBase API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
    });
    c.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// Infrastructure
builder.Services.AddMemoryCache();
builder.Services.Configure<PowerBase.Application.Common.Configurations.MetadataCacheOptions>(builder.Configuration.GetSection(PowerBase.Application.Common.Configurations.MetadataCacheOptions.SectionName));
// Per-tenant rate limit (API-01): RateLimiting:Enabled (off by default).
PowerBase.API.Middleware.TenantRateLimiting.AddTenantRateLimiting(builder.Services, builder.Configuration);
// SQL protection for the heaviest reads (whole-table scans): a few at a time per tenant DB, slowed under high Azure SQL load (doc 21).
builder.Services.Configure<PowerBase.Application.Common.Configurations.SqlWorkloadOptions>(builder.Configuration.GetSection(PowerBase.Application.Common.Configurations.SqlWorkloadOptions.SectionName));
builder.Services.AddSingleton<PowerBase.Infrastructure.Services.HeavyReadGovernor>();
// Optional L2 for the metadata cache (Azure Managed Redis, docs 23/34): MetadataCache:Redis:Host (Entra ID) or :ConnectionString. Off
// when neither is set. SQL versions stay the authority; the environment name is in every key, so environments never share entries.
PowerBase.Infrastructure.Services.MetadataRedisSetup.AddMetadataRedis(builder.Services, builder.Configuration, builder.Environment.EnvironmentName);
// ARC-01: the shared versions (meta.CacheVersion) that carry every cache invalidation to every API instance.
builder.Services.AddSingleton<PowerBase.Application.Common.Interfaces.ICacheVersionStore, PowerBase.Infrastructure.Services.SqlCacheVersionStore>();
builder.Services.AddSingleton<PowerBase.Application.Common.Interfaces.IMetadataCache, PowerBase.Infrastructure.Services.MetadataCache>();
builder.Services.AddSingleton<PowerBase.Application.Common.Interfaces.IRequestOriginRegistry, PowerBase.Infrastructure.Services.RequestOriginRegistry>();
builder.Services.Configure<PowerBase.API.Middleware.ServerTimingOptions>(builder.Configuration.GetSection(PowerBase.API.Middleware.ServerTimingOptions.SectionName));
builder.Services.AddScoped<PowerBase.Infrastructure.Services.ServerTiming>();
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IServerTiming>(sp => sp.GetRequiredService<PowerBase.Infrastructure.Services.ServerTiming>());
// JSON responses (report rows, field lists) compress to a fraction of their size; the SPA is on another origin, so this also matters over HTTPS.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("PipelineMakeRequest").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
});
builder.Services.AddSingleton<PowerBase.Application.Pipelines.IPipelineApiRequestDispatcher, PowerBase.API.Pipelines.PowerBaseApiRequestDispatcher>();
builder.Services.AddDataProtection();
builder.Services.AddScoped<PowerBase.API.Pipelines.RequestConnectionService>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.IRequestConnectionService>(services => services.GetRequiredService<PowerBase.API.Pipelines.RequestConnectionService>());
// Microsoft Outlook accounts for the PowerFlows "Send an Email" step.
builder.Services.AddHttpClient(PowerBase.API.Pipelines.OutlookConnectionService.HTTP_CLIENT_NAME, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient(PowerBase.Infrastructure.Services.OutlookGraphMailSender.HTTP_CLIENT_NAME, client => client.Timeout = TimeSpan.FromSeconds(100));
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IMicrosoftConnectionRepository, PowerBase.Infrastructure.Repositories.MicrosoftConnectionRepository>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Outlook.IOutlookGraphMailSender, PowerBase.Infrastructure.Services.OutlookGraphMailSender>();
builder.Services.AddScoped<PowerBase.API.Pipelines.OutlookConnectionService>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Outlook.IOutlookConnectionService>(services => services.GetRequiredService<PowerBase.API.Pipelines.OutlookConnectionService>());
var executionSection = builder.Configuration.GetSection("PipelineExecution");
builder.Services.Configure<PowerBase.Application.Common.Configurations.PipelineExecutionOptions>(executionSection);

var executionOptions = executionSection.Get<PowerBase.Application.Common.Configurations.PipelineExecutionOptions>() 
                       ?? new PowerBase.Application.Common.Configurations.PipelineExecutionOptions();
PowerBase.Application.Common.Configurations.PipelineExecutionOptionsValidator.Validate(executionOptions);

// Register Main DB queue repo
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IMainPipelineQueueRepository, PowerBase.Infrastructure.Repositories.MainPipelineQueueRepository>();

builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IPipelineEngine, PowerBase.Application.Pipelines.PipelineEngine>();
builder.Services.AddSingleton<PowerBase.Application.Pipelines.PipelineLoopWorkerPool>();
builder.Services.AddHostedService<PowerBase.API.Pipelines.PipelineSchedulerWorker>();

// Unconditional Database Queue Registrations
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IPipelineExecutionQueue, PowerBase.Infrastructure.Pipelines.DatabasePipelineExecutionQueue>();
builder.Services.AddHostedService<PowerBase.API.Pipelines.DatabasePipelineExecutionWorker>();
builder.Services.AddHostedService<PowerBase.API.Pipelines.DatabasePipelineOutboxRelay>();
builder.Services.AddHostedService<PowerBase.API.Pipelines.DatabasePipelineCleanupService>();
builder.Services.AddSingleton<DbConnectionFactory>(); // shim — kept for compatibility
builder.Services.AddSingleton<IControlConnectionFactory, ControlConnectionFactory>();
if (!string.IsNullOrEmpty(builder.Configuration["KeyVault:Uri"]))
{
    builder.Services.AddSingleton<KeyVaultSecretResolver>();
    builder.Services.AddSingleton<ISecretResolver>(sp => sp.GetRequiredService<KeyVaultSecretResolver>());
    builder.Services.AddSingleton<ISecretStore>(sp => sp.GetRequiredService<KeyVaultSecretResolver>());
}
else
{
    // Local dev: shared-server tenants work normally; BYO-server creation will throw clearly.
    builder.Services.AddSingleton<ISecretResolver, ConfigSecretResolver>();
    builder.Services.AddSingleton<ISecretStore, NoOpSecretStore>();
}
builder.Services.AddSingleton<ITenantConnectionResolver, TenantConnectionResolver>();
builder.Services.AddScoped<ITenantConnectionFactory, TenantConnectionFactory>();
// Read-replica lag check for CreateReadOnlyAsync. Unused while ReadReplica:Enabled is off (the default): every read stays on the primary.
builder.Services.AddSingleton<IReplicaLagMonitor, ReplicaLagMonitor>();
builder.Services.AddScoped<ControlUnitOfWork>();
builder.Services.AddScoped<IControlUnitOfWork>(sp => sp.GetRequiredService<ControlUnitOfWork>());
builder.Services.AddScoped<TenantUnitOfWork>();
builder.Services.AddScoped<ITenantUnitOfWork>(sp => new PowerBase.Infrastructure.UOW.TriggerPublishingTenantUnitOfWork(sp.GetRequiredService<TenantUnitOfWork>()));
builder.Services.AddScoped<UnitOfWork>();
builder.Services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ControlUnitOfWork>());

// Provisioning
builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

// Services
builder.Services.AddScoped<QueryContext>();
builder.Services.AddScoped<IQueryContext>(sp => sp.GetRequiredService<QueryContext>());
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IAppAccessService, AppAccessService>();
builder.Services.AddScoped<IRolePermissionEnforcer, RolePermissionEnforcer>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<PowerBase.Infrastructure.Services.OutlookInviteEmailService>();
builder.Services.AddScoped<IEmailService, PowerBase.Infrastructure.Services.OutlookInviteEmailService>();
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<ISchemaEngineService, SchemaEngineService>();
// Field Type Conversion ("Change Type") module — PowerBase.Application/Fields/TypeConversion
builder.Services.AddScoped<PowerBase.Application.Fields.TypeConversion.IFieldColumnConversionService, FieldColumnConversionService>();
builder.Services.AddScoped<PowerBase.Application.Fields.TypeConversion.FieldTypeChangeAnalyzer>();
builder.Services.AddScoped<PowerBase.Application.Fields.TypeConversion.FieldTypeChangePlanner>();
builder.Services.AddScoped<PowerBase.Application.Fields.TypeConversion.GetFieldTypeChangeTargetsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.TypeConversion.PreviewFieldTypeChangeQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.TypeConversion.ChangeFieldTypeCommandHandler>();
var storageProvider = builder.Configuration["Storage:Provider"] ?? "Local";
var azureBlobConnStr = builder.Configuration["Storage:AzureBlob:ConnectionString"] ?? builder.Configuration["AzureBlob:ConnectionString"];
bool hasValidAzureBlobConfig = !string.IsNullOrWhiteSpace(azureBlobConnStr) && 
                               !azureBlobConnStr.StartsWith("<") && 
                               (azureBlobConnStr.StartsWith("DefaultEndpointsProtocol=", StringComparison.OrdinalIgnoreCase) || azureBlobConnStr.Contains("AccountName="));

if (string.Equals(storageProvider, "AzureBlob", StringComparison.OrdinalIgnoreCase) && hasValidAzureBlobConfig)
{
    builder.Services.AddScoped<IFileStorageService, AzureBlobStorageService>();
}
else
{
    builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
}
// Records are decrypted value by value — a report page, a summary over every child of its parents, pipeline search — so
// every reader uses the DEK-caching service: same cipher and keys as AesEncryptionService, which it wraps to generate keys,
// without unwrapping the DEK again for each value.
builder.Services.AddSingleton<AesEncryptionService>();
builder.Services.AddSingleton<PowerBase.Infrastructure.Services.CachedDekEncryptionService>(sp =>
    new PowerBase.Infrastructure.Services.CachedDekEncryptionService(
        sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
        sp.GetRequiredService<AesEncryptionService>()));
builder.Services.AddSingleton<IEncryptionService>(sp => sp.GetRequiredService<PowerBase.Infrastructure.Services.CachedDekEncryptionService>());
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IPipelineRecordSearchService>(sp =>
    new PowerBase.Infrastructure.Services.PipelineRecordSearchService(
        sp.GetRequiredService<PowerBase.Infrastructure.Persistence.ITenantConnectionFactory>(),
        sp.GetRequiredService<PowerBase.Application.Common.Interfaces.IQueryContext>(),
        sp.GetRequiredService<PowerBase.Infrastructure.Services.CachedDekEncryptionService>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PowerBase.Application.Common.Configurations.PipelineExecutionOptions>>()));
builder.Services.AddSingleton<IAzureSearchService, PowerBase.Infrastructure.Services.AzureSearchService>();
builder.Services.AddScoped<PowerBase.Application.Records.RecordWriteService>();
builder.Services.AddScoped<PowerBase.Application.Records.ISnapshotCaptureService, PowerBase.Application.Records.SnapshotCaptureService>();
builder.Services.AddScoped<PowerBase.Application.Records.FileRecordWriteService>();
builder.Services.AddScoped<PowerBase.Application.Records.IRecordWriteService>(sp => sp.GetRequiredService<PowerBase.Application.Records.FileRecordWriteService>());
builder.Services.AddScoped<PowerBase.Application.Records.IFileRecordWriteService>(sp => sp.GetRequiredService<PowerBase.Application.Records.FileRecordWriteService>());
builder.Services.AddScoped<PowerBase.Application.Records.FileReservationService>();
builder.Services.AddScoped<IAppSeeder, AppSeeder>();
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IPipelineTriggerInterceptor, PowerBase.Infrastructure.Pipelines.PipelineTriggerInterceptor>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.IPipelineWriteTimeRelationalProjector, PowerBase.Infrastructure.Pipelines.PipelineWriteTimeRelationalProjector>();
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IPipelineAuditFormatter, PowerBase.Application.Pipelines.PipelineAuditFormatter>();
builder.Services.AddSingleton<IMessagePublisher, ServiceBusPublisher>();
builder.Services.AddScoped<IFieldNameResolver, PowerBase.Application.Common.Services.FieldNameResolver>();

// Field Settings Validators
builder.Services.AddScoped<IFieldSettingsValidator, TextSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, RichTextSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, EmailSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, PhoneSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, SelectSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, NumericSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, DateSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, TimeSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, DurationSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, UrlSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaUrlSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, DateRangeSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, NumericRangeSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, BooleanSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FileSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, AddressSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, UserFieldSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, MultiUserFieldSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaTextSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaNumericSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaDateSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaTimeSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaDurationSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaBooleanSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaPhoneSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaEmailSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaUserSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaRichTextSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaListSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, ReportLinkSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, ActionButtonSettingsValidator>();
builder.Services.AddScoped<FieldSettingsValidatorRegistry>();
builder.Services.AddScoped<IReportConfigValidator, TableReportConfigValidator>();
builder.Services.AddScoped<IReportConfigValidator, SummaryReportConfigValidator>();
builder.Services.AddScoped<IReportConfigValidator, ChartReportConfigValidator>();
builder.Services.AddScoped<ReportConfigValidatorRegistry>();

// Formula engine (stateless, shared) + compute-on-read projector + authoring query handlers
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<PowerBase.Formula.FormulaEngine>();
builder.Services.AddSingleton<PowerBase.Application.Common.Interfaces.IFormulaRuntimeContext, PowerBase.API.Services.FormulaRuntimeContext>();
builder.Services.AddScoped<PowerBase.Application.Formulas.IFormulaProjector, PowerBase.Application.Formulas.FormulaProjector>();
builder.Services.AddScoped<PowerBase.Application.Relationships.IRelationalProjector, PowerBase.Application.Relationships.RelationalProjector>();
builder.Services.AddScoped<PowerBase.Application.Formulas.Queries.ValidateFormulaQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Formulas.Queries.EvaluateFormulaQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Formulas.IFormulaExpressionValidator, PowerBase.Application.Formulas.FormulaExpressionValidator>();

// Action Buttons (Field-Type spec)
builder.Services.AddScoped<PowerBase.Application.Records.Commands.InvokeButtonAction.IActionButtonValueResolver,
    PowerBase.Application.Records.Commands.InvokeButtonAction.ActionButtonValueResolver>();
builder.Services.AddScoped<PowerBase.Application.Records.Commands.InvokeButtonAction.InvokeButtonActionCommandHandler>();

// Pages (shared layer — Phase 1)
builder.Services.AddScoped<PowerBase.Application.Pages.Commands.CreatePage.CreatePageCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Commands.UpdatePage.UpdatePageCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Commands.DeletePages.DeletePagesCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Commands.DuplicatePage.DuplicatePageCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Commands.PublishPage.PublishPageCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Commands.RestorePageVersion.RestorePageVersionCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Queries.ListPages.ListPagesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Queries.GetPage.GetPageQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Queries.ListPageVersions.ListPageVersionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Queries.RenderPage.RenderPageQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Commands.SetDefaultHome.SetDefaultHomeCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Queries.GetPageCode.GetPageCodeQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Queries.ListNavPages.ListNavPagesQueryHandler>();

// Repositories
builder.Services.AddScoped<IAppRepository, AppRepository>();
builder.Services.AddScoped<IAppCopyRepository, AppCopyRepository>();
builder.Services.AddSingleton<IAppCopyProgressStore, PowerBase.Infrastructure.Services.AppCopyProgressStore>();
builder.Services.AddScoped<IAppVariableRepository, AppVariableRepository>();
builder.Services.AddScoped<IImportSourceRefRepository, ImportSourceRefRepository>();
builder.Services.AddScoped<IAppFieldImportNoteRepository, AppFieldImportNoteRepository>();
builder.Services.AddScoped<IAppImportJobRepository, AppImportJobRepository>();
builder.Services.AddScoped<IEmailTemplateRepository, EmailTemplateRepository>();
// Durable control-DB queue for background app imports (any instance runs any queued job; survives restarts).
builder.Services.AddScoped<PowerBase.Application.Import.Jobs.IAppImportDispatchQueue, PowerBase.Infrastructure.Imports.AppImportDispatchQueue>();
PowerBase.Application.Import.Jobs.ImportRuntime.ConfiguredCommandTimeoutSeconds =
    builder.Configuration.GetValue("Import:SqlCommandTimeoutSeconds", PowerBase.Application.Import.Jobs.ImportRuntime.DEFAULT_COMMAND_TIMEOUT_SECONDS);
builder.Services.AddScoped<PowerBase.Application.Import.Jobs.AppImportJobService>();
builder.Services.AddScoped<PowerBase.Application.Import.Jobs.EmailTemplateService>();
builder.Services.AddScoped<PowerBase.Application.Import.Jobs.AppImportJobRunner>();
builder.Services.AddHostedService<PowerBase.API.Workers.AppImportJobWorker>();
builder.Services.AddScoped<IAppRoleRepository, AppRoleRepository>();
builder.Services.AddScoped<IAppRolePermissionRepository, AppRolePermissionRepository>();
builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();
builder.Services.AddScoped<IAppTableRepository, AppTableRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<ISystemRoleRepository, SystemRoleRepository>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<IAppFieldRepository, AppFieldRepository>();
builder.Services.AddScoped<IFieldVersionRepository, FieldVersionRepository>();
builder.Services.AddScoped<IFieldTypeRepository, FieldTypeRepository>();
builder.Services.AddScoped<IRecordRepository, RecordRepository>();
builder.Services.AddScoped<ISearchOutbox, SearchOutboxRepository>();
// Stored columns (c_{fid}) for Lookup/Summary/Formula: change capture only. Writes nothing until Compute:Enabled AND the tenant's
// ComputeEnabled flag are both on; the work itself runs in PowerBase.ComputeWorker, never in the API.
builder.Services.AddSingleton<PowerBase.Application.Common.Interfaces.IComputeSettings, PowerBase.Infrastructure.Compute.ComputeSettings>();
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IComputeOutbox, PowerBase.Infrastructure.Compute.ComputeOutboxRepository>();
builder.Services.AddSingleton<PowerBase.Application.Common.Interfaces.IComputeQueuePublisher, PowerBase.Infrastructure.Compute.ServiceBusComputePublisher>();
// Read side: which stored columns are Ready (a few seconds' cache, independent of the field metadata cache) and the 12B invalidation.
builder.Services.AddScoped<PowerBase.Application.Compute.IMaterializedFieldState, PowerBase.Infrastructure.Compute.MaterializedFieldStateRepository>();
builder.Services.AddScoped<ISearchFieldJobs, SearchFieldJobRepository>();
// Stored Lookup / Summary / Formula fields: Indexing -> Indexed once their field job is done (migrations 101 / 103).
builder.Services.AddScoped<ISearchIndexStates, PowerBase.Infrastructure.Repositories.SearchIndexStateRepository>();

// Import (Table-to-Table / file) — see PowerBase.Application/Imports
builder.Services.AddScoped<PowerBase.Application.Imports.IImportDefinitionRepository, PowerBase.Infrastructure.Imports.ImportDefinitionRepository>();
builder.Services.AddScoped<PowerBase.Application.Imports.IImportRunRepository, PowerBase.Infrastructure.Imports.ImportRunRepository>();
builder.Services.AddScoped<PowerBase.Application.Imports.IImportFileRepository, PowerBase.Infrastructure.Imports.ImportFileRepository>();
builder.Services.AddScoped<PowerBase.Application.Imports.Files.IImportFileAccess, PowerBase.Application.Imports.Files.ImportFileAccess>();
builder.Services.AddScoped<PowerBase.Application.Imports.Files.UploadImportFileHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.Files.PreviewImportFileHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.Files.DiscardImportFileHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.Files.ImportFileCleanup>();
builder.Services.AddScoped<PowerBase.Application.Imports.ImportRetentionCleanup>();
builder.Services.Configure<PowerBase.Application.Common.Configurations.ImportOptions>(builder.Configuration.GetSection(PowerBase.Application.Common.Configurations.ImportOptions.SectionName));
builder.Services.AddScoped<PowerBase.Application.Imports.IImportQueue, PowerBase.Infrastructure.Imports.ImportQueue>();
builder.Services.AddScoped<PowerBase.Application.Imports.IImportDataStore, PowerBase.Infrastructure.Imports.ImportDataStore>();
builder.Services.AddScoped<PowerBase.Application.Imports.ImportPlanBuilder>();
builder.Services.AddScoped<PowerBase.Application.Imports.ImportSourceReader>();
builder.Services.AddScoped<PowerBase.Application.Imports.ImportMultiTargetRunner>();
builder.Services.AddScoped<PowerBase.Application.Imports.ImportRunProcessor>();
builder.Services.AddScoped<PowerBase.Application.Imports.SaveImportDefinitionHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.StartQuickImportHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.SetImportScheduleEnabledHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.ListImportDefinitionsHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.ListPowerFlowImportTemplatesHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.IPowerFlowImportActions, PowerBase.Application.Imports.PowerFlowImportActions>();
builder.Services.AddScoped<PowerBase.Application.Imports.StartPowerFlowImportHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.ListAppImportDefinitionsHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.ValidateImportFormulaHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.ValidatePipelineFormula.ValidatePipelineFormulaHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.ListImportHistoryHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.Files.ColumnFitHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.IReferenceOriginalFields, PowerBase.Application.Imports.ReferenceOriginalFields>();
builder.Services.AddScoped<PowerBase.Application.Imports.Files.PreviewImportSheetsHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.GetImportSourceFileHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.DeleteImportDefinitionsHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.GetImportDefinitionHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.DeleteImportDefinitionHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.StartImportRunHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.ListImportRunsHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.GetImportRunHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.GetImportFeedbackHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.CancelImportRunHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.ListMyImportRunsHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.GetImportRunStatusHandler>();
builder.Services.AddScoped<PowerBase.Application.Imports.IImportNotifier, PowerBase.Application.Imports.ImportCompletionNotifier>();
builder.Services.AddHostedService<PowerBase.API.Workers.ImportExecutionWorker>();
builder.Services.AddScoped<PowerBase.Application.Imports.IImportDefinitionChecker, PowerBase.Application.Imports.ImportDefinitionChecker>();
builder.Services.AddScoped<PowerBase.Application.Imports.ImportScheduleDispatcher>();
builder.Services.AddHostedService<PowerBase.API.Workers.ImportSchedulerWorker>();
builder.Services.AddScoped<IReportRepository, ReportRepository>();
builder.Services.AddScoped<IFieldReferenceRepository, FieldReferenceRepository>();
builder.Services.AddScoped<PowerBase.Application.FieldReferences.IFieldReferenceIndexer, PowerBase.Application.FieldReferences.FieldReferenceIndexer>();
builder.Services.AddScoped<PowerBase.Application.FieldReferences.RebuildFieldReferencesCommandHandler>();
builder.Services.AddHostedService<PowerBase.API.Workers.PipelineReferenceSyncWorker>();
builder.Services.AddScoped<PowerBase.Application.Search.Commands.BackfillSearchIndex.BackfillSearchIndexCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Search.Commands.SanitizeEncryptedData.SanitizeEncryptedDataCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IExemptFieldEncryptionCleanupService, PowerBase.Infrastructure.Services.ExemptFieldEncryptionCleanupService>();
builder.Services.AddScoped<PowerBase.Application.Fields.Commands.CleanupExemptFieldEncryption.CleanupExemptFieldEncryptionCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IDatabaseDecryptionService, PowerBase.Infrastructure.Services.DatabaseDecryptionService>();
builder.Services.AddSingleton<PowerBase.Application.Common.Interfaces.DecryptionJobTracker>();
builder.Services.AddHostedService<PowerBase.API.Workers.DecryptDatabaseOnStartupWorker>();
builder.Services.AddScoped<IPermissionRepository, PermissionRepository>();
builder.Services.AddScoped<IUserPermissionRepository, UserPermissionRepository>();
builder.Services.AddScoped<IFormRepository, FormRepository>();
builder.Services.AddScoped<IFormRuleRepository, FormRuleRepository>();
builder.Services.AddScoped<IRelationshipRepository, RelationshipRepository>();
builder.Services.AddScoped<IAdminRepository, AdminRepository>();
builder.Services.AddScoped<IPageRepository, PageRepository>();
builder.Services.AddScoped<IPipelineRepository, PipelineRepository>();
builder.Services.AddScoped<IPipelineStepIdempotencyRepository, PipelineStepIdempotencyRepository>();
builder.Services.AddScoped<IUserTokenRepository, UserTokenRepository>();
builder.Services.AddScoped<IAppTokenRepository, AppTokenRepository>();
builder.Services.AddScoped<IPipelineAccountRepository, PipelineAccountRepository>();
builder.Services.AddScoped<IGroupRepository, GroupRepository>();
builder.Services.AddScoped<ICapabilityRepository, CapabilityRepository>();


// Handlers
builder.Services.AddScoped<PowerBase.Application.Pipelines.Commands.CreatePipeline.CreatePipelineCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Commands.CopyPipeline.CopyPipelineCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Commands.UpdatePipeline.UpdatePipelineCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Commands.SavePipelineSteps.SavePipelineStepsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Commands.DeletePipeline.DeletePipelineCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Commands.DeletePipelines.DeletePipelinesCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.GetPipeline.GetPipelineQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.GetPipelineEditor.GetPipelineEditorQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.ListPipelines.ListPipelinesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.ListPipelineRuns.ListPipelineRunsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.ListPipelineRuns.ListAppPipelineRunsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.GetPipelineRunSteps.GetPipelineRunStepsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.GetPipelineActivity.GetPipelineActivityQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.GetCopyFailedRecords.GetCopyFailedRecordsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.ListPipelinesForPicker.ListPipelinesForPickerQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Queries.GetPipelineSchedule.GetPipelineScheduleQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule.UpdatePipelineScheduleCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pipelines.Commands.DeletePipelineSchedule.DeletePipelineScheduleCommandHandler>();

// Saved PowerFlows accounts ("Connect new account")
builder.Services.AddScoped<PowerBase.Application.Connections.Common.ConnectionScopeResolver>();
builder.Services.AddScoped<PowerBase.Application.Connections.Queries.GetConnections.GetConnectionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Connections.Commands.CreateConnection.CreateConnectionCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Connections.Queries.GetConnectionApps.GetConnectionAppsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Connections.Queries.GetConnectionTables.GetConnectionTablesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Connections.Queries.GetConnectionFields.GetConnectionFieldsQueryHandler>();

builder.Services.AddScoped<PowerBase.Application.UserTokens.Commands.CreateUserToken.CreateUserTokenCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.UserTokens.Queries.GetMyUserTokens.GetMyUserTokensQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.UserTokens.Queries.GetAdminUserTokens.GetAdminUserTokensQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.UserTokens.Queries.GetSingleTokenDetail.GetSingleTokenDetailQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.UserTokens.Commands.UpdateUserTokenStatus.UpdateUserTokenStatusCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.UserTokens.Commands.UpdateUserToken.UpdateUserTokenCommandHandler>();

builder.Services.AddScoped<PowerBase.Application.UserTokens.Commands.RevokeUserToken.RevokeUserTokenCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.UserTokens.Commands.RotateUserToken.RotateUserTokenCommandHandler>();

builder.Services.AddScoped<PowerBase.Application.AppTokens.Commands.CreateAppToken.CreateAppTokenCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.AppTokens.Queries.GetAppTokens.GetAppTokensQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.AppTokens.Commands.UpdateAppTokenStatus.UpdateAppTokenStatusCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.AppTokens.Commands.RotateAppToken.RotateAppTokenCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.AppTokens.Commands.DeleteAppToken.DeleteAppTokenCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.AppTokens.Commands.BulkDeleteAppTokens.BulkDeleteAppTokensCommandHandler>();

builder.Services.AddScoped<SignupCommandHandler>();
builder.Services.AddScoped<ForgotPasswordCommandHandler>();
builder.Services.AddScoped<ResetPasswordCommandHandler>();
builder.Services.AddScoped<AcceptInviteCommandHandler>();
builder.Services.AddScoped<LoginQueryHandler>();
builder.Services.AddScoped<GetMeQueryHandler>();
builder.Services.AddScoped<GetMyPreferencesQueryHandler>();
builder.Services.AddScoped<UpdateMyPreferencesCommandHandler>();
builder.Services.AddScoped<UpdateUserProfileCommandHandler>();
builder.Services.AddScoped<SelectTenantCommandHandler>();
builder.Services.AddScoped<RefreshTokenCommandHandler>();
builder.Services.AddScoped<CreateTenantCommandHandler>();
builder.Services.AddScoped<CreateAppCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.CopyApp.CopyAppCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetAppCopyProgress.GetAppCopyProgressQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.ListMyAppCopies.ListMyAppCopiesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.DismissAppCopy.DismissAppCopyCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Import.Pbl.PblValidator>();
builder.Services.AddScoped<PowerBase.Application.Import.FormulaTranslation.FormulaTranslator>();
builder.Services.Configure<PowerBase.Application.Import.Quickbase.QuickbaseImportOptions>(
    builder.Configuration.GetSection(PowerBase.Application.Import.Quickbase.QuickbaseImportOptions.SectionName));
builder.Services.AddScoped<PowerBase.Application.Common.Interfaces.IQuickbaseCatalogRepository, PowerBase.Infrastructure.Repositories.QuickbaseCatalogRepository>();
builder.Services.AddScoped<PowerBase.Application.Import.Quickbase.IQuickbaseTablesApi, PowerBase.Application.Import.Quickbase.QuickbaseTablesApi>();
builder.Services.AddScoped<PowerBase.Application.Import.Quickbase.QuickbaseIdResolver>();
builder.Services.AddScoped<PowerBase.Application.Import.Queries.ImportPreview.ImportPreviewQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.PblExtrasImporter>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.PblDeferredResolver>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.ImportFieldSanitizer>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.ImportFieldNoteRecorder>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.PblSettingsApplier>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.PblPermissionsApplier>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.PblConnectedTablesApplier>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.PendingConnectedTableResolver>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.PblPipelinesImporter>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.PipelineOnlyImportService>();
builder.Services.AddScoped<PowerBase.Application.Import.Diff.ImportDiffBuilder>();
builder.Services.AddScoped<PowerBase.Application.Import.Queries.ImportDiff.ImportDiffQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Import.Commands.UpdateAppFromPbl.UpdateAppFromPblCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Import.Services.ImportSourceRefRecorder>();
builder.Services.AddScoped<PowerBase.Application.Import.Commands.ImportAppFromPbl.ImportAppFromPblCommandHandler>();
builder.Services.AddScoped<UpdateAppCommandHandler>();
builder.Services.AddScoped<ListAppUsersQueryHandler>();
builder.Services.AddScoped<ListAppUsersForPickerQueryHandler>();
builder.Services.AddScoped<AddAppUserCommandHandler>();
builder.Services.AddScoped<InviteAppUserCommandHandler>();
builder.Services.AddScoped<ChangeAppUserRoleCommandHandler>();
builder.Services.AddScoped<UpdateUserPickerVisibilityCommandHandler>();
builder.Services.AddScoped<RemoveAppUserCommandHandler>();
builder.Services.AddScoped<ListAppRolesQueryHandler>();
builder.Services.AddScoped<CreateAppRoleCommandHandler>();
builder.Services.AddScoped<DeleteAppRoleCommandHandler>();
builder.Services.AddScoped<UpdateAppRoleCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.SetDefaultAppRole.SetDefaultAppRoleCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.SetAppRoleHomePage.SetAppRoleHomePageCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Pages.Queries.ResolveHomePage.ResolveHomePageQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetTablePermissions.GetTablePermissionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.UpdateTablePermissions.UpdateTablePermissionsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetFieldPermissions.GetFieldPermissionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.UpdateFieldPermissions.UpdateFieldPermissionsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetRecordFilters.GetRecordFiltersQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.UpdateRecordFilters.UpdateRecordFiltersCommandHandler>();
builder.Services.AddScoped<DeleteAppCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.BulkDeleteApps.BulkDeleteAppsCommandHandler>();
builder.Services.AddScoped<GetAppQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetAppStorageUsage.GetAppStorageUsageQueryHandler>();
builder.Services.AddScoped<ListAppsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetAppPermissions.GetAppPermissionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetMyAppRoles.GetMyAppRolesQueryHandler>();
builder.Services.AddScoped<ListAppVariablesQueryHandler>();
builder.Services.AddScoped<CreateAppVariableCommandHandler>();
builder.Services.AddScoped<UpdateAppVariableCommandHandler>();
builder.Services.AddScoped<DeleteAppVariableCommandHandler>();
builder.Services.AddScoped<GetAppBrandingQueryHandler>();
builder.Services.AddScoped<UpdateAppBrandingCommandHandler>();
builder.Services.AddScoped<CreateTableCommandHandler>();
builder.Services.AddScoped<UpdateTableCommandHandler>();
builder.Services.AddScoped<DeleteTableCommandHandler>();
builder.Services.AddScoped<BulkDeleteTablesCommandHandler>();
builder.Services.AddScoped<SetTablesShowInBarCommandHandler>();
builder.Services.AddScoped<GetTableQueryHandler>();
builder.Services.AddScoped<ListTablesQueryHandler>();
builder.Services.AddScoped<ListTableNavItemsQueryHandler>();
builder.Services.AddScoped<CreateFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Commands.BulkCreateFields.BulkCreateFieldsCommandHandler>();
builder.Services.AddScoped<UpdateFieldCommandHandler>();
builder.Services.AddScoped<DeleteFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Commands.BulkDeleteFields.BulkDeleteFieldsCommandHandler>();
builder.Services.AddScoped<RestoreFieldVersionCommandHandler>();
builder.Services.AddScoped<FieldSettingsGuard>();
builder.Services.AddScoped<FieldVersionService>();
builder.Services.AddScoped<ListFieldsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Queries.ListFields.ListAllFieldsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Queries.GetField.GetFieldQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.ImportNotes.FieldImportNotesHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Queries.ListFieldTypes.ListFieldTypesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Queries.GetFieldUsage.GetFieldUsageQueryHandler>();
builder.Services.AddScoped<ListFieldVersionsQueryHandler>();
builder.Services.AddScoped<GetFieldVersionDetailQueryHandler>();
builder.Services.AddScoped<CreateRecordCommandHandler>();
builder.Services.AddScoped<UpdateRecordCommandHandler>();
builder.Services.AddScoped<DeleteRecordCommandHandler>();
builder.Services.AddScoped<BulkDeleteRecordsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Records.Commands.BulkCreateRecords.BulkCreateRecordsCommandHandler>();
builder.Services.AddScoped<MassUpdateRecordsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Records.RecordModifyGuard>();
builder.Services.AddScoped<PowerBase.Application.Records.Queries.GetModifiableRecordIds.GetModifiableRecordIdsQueryHandler>();
builder.Services.AddScoped<ListRecordsQueryHandler>();
builder.Services.AddScoped<GetRecordQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Records.Queries.SearchGlobalRecords.SearchGlobalRecordsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Search.Commands.BackfillSearchIndex.BackfillSearchIndexCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Search.Commands.SanitizeEncryptedData.SanitizeEncryptedDataCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Records.Queries.GetDistinctFieldValues.GetDistinctFieldValuesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Queries.SharedValues.ListSharedFieldsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Queries.SharedValues.GetSharedFieldValuesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.CreateRelationship.CreateRelationshipCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.DeleteRelationship.DeleteRelationshipCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.AddLookupFields.AddLookupFieldsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.AddSummaryField.AddSummaryFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.UpdateSummaryField.UpdateSummaryFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.RemoveRelationshipField.RemoveRelationshipFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.UpdateDisplayKey.UpdateDisplayKeyCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.UpdateReferenceFilter.UpdateReferenceFilterCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.UpdateProxyField.UpdateProxyFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.UpdateParentLink.UpdateParentLinkCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.AddFormSection.AddRelationshipFormSectionCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.UpdateLookupSource.UpdateLookupSourceCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Queries.GetLookupSourceOptionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.RelationshipFieldFactory>();
builder.Services.AddScoped<PowerBase.Application.Relationships.RelationshipKeyCarryOverService>();
builder.Services.AddScoped<PowerBase.Application.Relationships.IReferenceRelinkService, PowerBase.Application.Relationships.ReferenceRelinkService>();
builder.Services.AddScoped<PowerBase.Application.Relationships.IReferenceRelinkQueue, PowerBase.Infrastructure.Repositories.ReferenceRelinkQueue>();
builder.Services.AddHostedService<PowerBase.API.Workers.ReferenceRelinkWorker>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Queries.RelationshipQueriesHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Queries.GetParentOptionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Queries.IsParentAllowedQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Queries.GetControllingFillQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Queries.GetParentRecordQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Queries.GetChildRecordsForParentQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Commands.SetKey.SetKeyCommandHandler>();
builder.Services.AddScoped<CreateReportCommandHandler>();
builder.Services.AddScoped<UpdateReportCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Commands.DeleteReportRecords.DeleteReportRecordsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.DeleteJobs.IRecordDeleteJobRepository, PowerBase.Infrastructure.Repositories.RecordDeleteJobRepository>();
builder.Services.AddScoped<PowerBase.Application.Reports.DeleteJobs.RecordDeleteJobProcessor>();
builder.Services.AddHostedService<PowerBase.API.Workers.RecordDeleteJobWorker>();
builder.Services.AddScoped<PowerBase.Application.Fields.SnapshotJobs.IFieldSnapshotJobRepository, PowerBase.Infrastructure.Repositories.FieldSnapshotJobRepository>();
builder.Services.AddScoped<PowerBase.Application.Fields.SnapshotJobs.FieldSnapshotJobProcessor>();
builder.Services.AddScoped<PowerBase.Application.Relationships.LookupResultTypeRepairService>();
builder.Services.AddScoped<PowerBase.Application.Fields.SnapshotJobs.FieldSnapshotJobService>();
builder.Services.AddHostedService<PowerBase.API.Workers.FieldSnapshotJobWorker>();
builder.Services.AddScoped<DeleteReportCommandHandler>();
builder.Services.AddScoped<SetDefaultReportCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Commands.UpdateReportFormOverrides.UpdateReportFormOverridesCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Queries.GetReportGridEditRules.GetReportGridEditRulesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Commands.UpdateReportGridEditRules.UpdateReportGridEditRulesCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Queries.GetGridEditFormRules.GetGridEditFormRulesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Queries.GetReportGridEditRuntime.GetReportGridEditRuntimeQueryHandler>();
builder.Services.AddScoped<UpdateDefaultReportSettingsCommandHandler>();
builder.Services.AddScoped<GetReportQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Queries.GetOrCreateDefaultReportSettings.GetOrCreateDefaultReportSettingsQueryHandler>();
builder.Services.AddScoped<GetDefaultReportSettingsQueryHandler>();
builder.Services.AddScoped<ListReportsQueryHandler>();
builder.Services.AddScoped<ListReportsByTableQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Queries.ListReportsByTablePaged.ListReportsByTablePagedQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Queries.ListReportPicker.ListReportPickerQueryHandler>();
builder.Services.AddScoped<ResolveDefaultReportQueryHandler>();
builder.Services.AddScoped<RunReportQueryHandler>();
builder.Services.AddScoped<ExportReportQueryHandler>();
// Quickbase-compatible API v1 (QuickbaseApi/V1) — new services only; existing registrations unchanged.
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.V1.Services.QbSchemaService>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.Common.QbIdResolver>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.V1.Services.QbReferenceTypes>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.V1.Services.QbReportService>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.V1.Services.QbUserValueResolver>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.V1.Services.QbRecordService>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.V1.Services.QbFileService>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.Xml.QbXmlDispatcher>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.Xml.QbXmlRecordActions>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.Xml.QbXmlQueryActions>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.Xml.QbXmlUserActions>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.Xml.QbUserProvisioning>();
builder.Services.AddScoped<PowerBase.API.QuickbaseApi.Xml.QbXmlImportActions>();
builder.Services.AddScoped<PowerBase.Application.Reports.Queries.GetRolesReportsMatrix.GetRolesReportsMatrixQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Commands.UpdateReportVisibilityMatrix.UpdateReportVisibilityMatrixCommandHandler>();
builder.Services.AddScoped<ListUsersQueryHandler>();
builder.Services.AddScoped<InviteUserCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Admin.Commands.AdminInviteUserCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Admin.Commands.AdminInvitePlatformUserCommandHandler>();
builder.Services.AddScoped<ChangeUserRoleCommandHandler>();
builder.Services.AddScoped<RemoveUserCommandHandler>();
builder.Services.AddScoped<ListRolesQueryHandler>();
builder.Services.AddScoped<CreateRoleCommandHandler>();
builder.Services.AddScoped<UpdateRoleCommandHandler>();
builder.Services.AddScoped<DeleteRoleCommandHandler>();
builder.Services.AddScoped<GetRolePermissionsQueryHandler>();
builder.Services.AddScoped<UpdateRolePermissionsCommandHandler>();
builder.Services.AddScoped<ListPermissionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Capabilities.Queries.ListCapabilities.ListCapabilitiesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Capabilities.Queries.GetRoleCapabilities.GetRoleCapabilitiesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Capabilities.Commands.SaveRoleCapabilities.SaveRoleCapabilitiesCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Capabilities.Commands.UpdateRoleCapability.UpdateRoleCapabilityCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Queries.GetReportPreviewMetadata.GetReportPreviewMetadataQueryHandler>();
builder.Services.AddScoped<ListAuditLogsQueryHandler>();
builder.Services.AddScoped<ExportAuditLogsCsvQueryHandler>();
builder.Services.AddScoped<CreateFormCommandHandler>();
builder.Services.AddScoped<UpdateFormSettingsCommandHandler>();
builder.Services.AddScoped<SaveFormLayoutCommandHandler>();
builder.Services.AddScoped<DeleteFormCommandHandler>();
builder.Services.AddScoped<DuplicateFormCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Forms.Commands.SetDefaultForm.SetDefaultFormCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Forms.Commands.SetQuickPeekForm.SetQuickPeekFormCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Forms.Commands.UpdateRoleFormOverrides.UpdateRoleFormOverridesCommandHandler>();
builder.Services.AddScoped<CreateFormRuleCommandHandler>();
builder.Services.AddScoped<SaveFormRuleCommandHandler>();
builder.Services.AddScoped<DeleteFormRuleCommandHandler>();
builder.Services.AddScoped<DuplicateFormRuleCommandHandler>();
builder.Services.AddScoped<ReorderFormRulesCommandHandler>();
builder.Services.AddScoped<ToggleFormRuleCommandHandler>();
builder.Services.AddScoped<GetFormQueryHandler>();
builder.Services.AddScoped<ListFormsQueryHandler>();
builder.Services.AddScoped<GetFormLayoutQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Forms.Queries.GetRoleFormOverrides.GetRoleFormOverridesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Forms.Queries.ResolveForm.ResolveFormQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Forms.Queries.GetQuickPeekForm.GetQuickPeekFormQueryHandler>();
builder.Services.AddScoped<ListFormRulesQueryHandler>();
builder.Services.AddScoped<GetFormRuleQueryHandler>();

// Groups
builder.Services.AddScoped<CreateGroupCommandHandler>();
builder.Services.AddScoped<UpdateGroupCommandHandler>();
builder.Services.AddScoped<DeleteGroupCommandHandler>();
builder.Services.AddScoped<ListGroupsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Groups.Queries.GetMyGroups.GetMyGroupsQueryHandler>();
builder.Services.AddScoped<GetGroupQueryHandler>();
builder.Services.AddScoped<AddGroupMembersCommandHandler>();
builder.Services.AddScoped<RemoveGroupMemberCommandHandler>();
builder.Services.AddScoped<ListGroupMembersQueryHandler>();
builder.Services.AddScoped<GetUserEffectivePermissionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Groups.Commands.ShareGroupWithApp.ShareGroupWithAppCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Groups.Commands.UnshareGroupFromApp.UnshareGroupFromAppCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Groups.Queries.GetSharedApps.GetSharedAppsQueryHandler>();

if (builder.Configuration.GetValue<bool>("Search:Indexer:Enabled", true))
{
    builder.Services.AddHostedService<PowerBase.API.Workers.SearchIndexerWorker>();
    builder.Services.AddHostedService<PowerBase.API.Workers.SearchIndexerPriorityWorker>();
}

// Makes sure no record change is lost on the way to the search index: re-publishes changes the indexer has not confirmed, and checks daily.
builder.Services.AddSingleton<PowerBase.Infrastructure.Services.SearchPipelineState>();
builder.Services.AddHostedService<PowerBase.API.Workers.SearchOutboxRelay>();
builder.Services.AddHealthChecks()
    .AddCheck<PowerBase.API.Services.SearchPipelineHealthCheck>("search-pipeline")
    // Load balancer readiness (/health/ready): started + control database reachable. /health/live has no checks (process is up).
    .AddCheck<PowerBase.API.Services.ReadinessHealthCheck>("ready", tags: ["ready"]);

// Connected Tables: a standalone module (its own services, read-only write filter and workers).
PowerBase.API.ConnectedTables.ConnectedTablesApiExtensions.AddConnectedTablesModule(builder.Services, builder.Configuration);

// Startup warm-up: /health/ready answers Healthy only after it (pools, DI graph, JIT), so a new instance gets traffic warm.
builder.Services.AddHostedService<PowerBase.API.Services.StartupWarmup>();

// BackgroundWorkers:Enabled=false: this instance serves requests only — every background loop registered above (pipelines, imports,
// search indexing, jobs, schedulers, connected tables) is left out. For a separate worker host or a request-only slot; all of them are
// safe to run on several instances at once (SQL leases / locks), so the default stays true.
if (!builder.Configuration.GetValue("BackgroundWorkers:Enabled", true))
{
    var background = builder.Services
        .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                    && d.ImplementationType?.Namespace is { } ns && ns.StartsWith("PowerBase.", StringComparison.Ordinal)
                    && d.ImplementationType != typeof(PowerBase.API.Services.StartupWarmup))
        .ToList();
    foreach (var d in background) builder.Services.Remove(d);
}

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseResponseCompression();
app.UseCors("Frontend");
app.UseMiddleware<PowerBase.API.Middleware.ServerTimingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Serve statically uploaded files from the configured local path
var localPath = app.Configuration.GetValue<string>("Storage:LocalPath") ?? "C:\\PowerbaseUploads";
if (!System.IO.Directory.Exists(localPath))
{
    System.IO.Directory.CreateDirectory(localPath);
}
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(localPath),
    RequestPath = "/files"
});

app.UseMiddleware<QueryContextMiddleware>();
app.UseMiddleware<PowerBase.API.Middleware.CacheVersionScopeMiddleware>();
// Quickbase XML API (/db/*): copies a usertoken/ticket request parameter into the Authorization header. Other paths untouched.
app.UseMiddleware<PowerBase.API.QuickbaseApi.Xml.QbXmlTokenMiddleware>();
app.UseMiddleware<JwtMiddleware>();
app.UseMiddleware<PowerBase.API.Middleware.RequestOriginMiddleware>();
PowerBase.API.Middleware.TenantRateLimiting.UseTenantRateLimiting(app);   // after JwtMiddleware: the tenant is known
app.MapControllers();
// Liveness of the search pipeline (workers, outbox backlog, search service): counts and times only, no record data. Point an uptime probe
// or Azure Monitor availability test at it.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapHealthChecks("/health/search", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Name == "search-pipeline",
    ResponseWriter = PowerBase.API.Services.SearchPipelineHealthCheck.WriteResponseAsync,
    ResultStatusCodes =
    {
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});

app.Run();

public partial class Program { }
