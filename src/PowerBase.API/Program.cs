using PowerBase.API.Middleware;
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
using PowerBase.Application.Auth.Queries.GetMe;
using PowerBase.Application.Auth.Queries.Login;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tenants.Commands.CreateTenant;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Commands.DeleteField;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Queries.ListFields;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Records.Commands.CreateRecord;
using PowerBase.Application.Records.Commands.DeleteRecord;
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
using PowerBase.Application.Roles.Commands.CreateRole;
using PowerBase.Application.Roles.Commands.DeleteRole;
using PowerBase.Application.Roles.Commands.UpdateRole;
using PowerBase.Application.Roles.Commands.UpdateRolePermissions;
using PowerBase.Application.Roles.Queries.GetRolePermissions;
using PowerBase.Application.Roles.Queries.ListPermissions;
using PowerBase.Application.Roles.Queries.ListRoles;
using PowerBase.Application.Tables.Commands.CreateTable;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Application.Tables.Commands.UpdateTable;
using PowerBase.Application.Tables.Queries.GetTable;
using PowerBase.Application.Tables.Queries.ListTables;
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

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

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
builder.Services.AddScoped<ControlUnitOfWork>();
builder.Services.AddScoped<IControlUnitOfWork>(sp => sp.GetRequiredService<ControlUnitOfWork>());
builder.Services.AddScoped<TenantUnitOfWork>();
builder.Services.AddScoped<ITenantUnitOfWork>(sp => sp.GetRequiredService<TenantUnitOfWork>());
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
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<ISchemaEngineService, SchemaEngineService>();
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
builder.Services.AddScoped<PowerBase.Application.Records.IRecordWriteService, PowerBase.Application.Records.RecordWriteService>();

// Field Settings Validators
builder.Services.AddScoped<IFieldSettingsValidator, TextSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, NumberSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, CurrencySettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, PercentSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, RatingSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, DateSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, DurationSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, UrlSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, DateRangeSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, NumericRangeSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, FormulaSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, ReportLinkSettingsValidator>();
builder.Services.AddScoped<IFieldSettingsValidator, ActionButtonSettingsValidator>();
builder.Services.AddScoped<FieldSettingsValidatorRegistry>();

// Formula engine (stateless, shared) + compute-on-read projector + authoring query handlers
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<PowerBase.Formula.FormulaEngine>();
builder.Services.AddSingleton<PowerBase.Application.Common.Interfaces.IFormulaRuntimeContext, PowerBase.API.Services.FormulaRuntimeContext>();
builder.Services.AddScoped<PowerBase.Application.Formulas.IFormulaProjector, PowerBase.Application.Formulas.FormulaProjector>();
builder.Services.AddScoped<PowerBase.Application.Relationships.IRelationalProjector, PowerBase.Application.Relationships.RelationalProjector>();
builder.Services.AddScoped<PowerBase.Application.Formulas.Queries.ValidateFormulaQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Formulas.Queries.EvaluateFormulaQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Formulas.IFormulaDefaultResolver, PowerBase.Application.Formulas.FormulaDefaultResolver>();
builder.Services.AddScoped<PowerBase.Application.Formulas.IFormulaExpressionValidator, PowerBase.Application.Formulas.FormulaExpressionValidator>();

// Action Buttons (Field-Type spec)
builder.Services.AddScoped<PowerBase.Application.Records.Commands.InvokeButtonAction.IActionButtonValueResolver,
    PowerBase.Application.Records.Commands.InvokeButtonAction.ActionButtonValueResolver>();
builder.Services.AddScoped<PowerBase.Application.Records.Commands.InvokeButtonAction.InvokeButtonActionCommandHandler>();

// Repositories
builder.Services.AddScoped<IAppRepository, AppRepository>();
builder.Services.AddScoped<IAppVariableRepository, AppVariableRepository>();
builder.Services.AddScoped<IAppRoleRepository, AppRoleRepository>();
builder.Services.AddScoped<IAppRolePermissionRepository, AppRolePermissionRepository>();
builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();
builder.Services.AddScoped<IAppTableRepository, AppTableRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<ISystemRoleRepository, SystemRoleRepository>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<IAppFieldRepository, AppFieldRepository>();
builder.Services.AddScoped<IFieldTypeRepository, FieldTypeRepository>();
builder.Services.AddScoped<IRecordRepository, RecordRepository>();
builder.Services.AddScoped<IReportRepository, ReportRepository>();
builder.Services.AddScoped<IPermissionRepository, PermissionRepository>();
builder.Services.AddScoped<IUserPermissionRepository, UserPermissionRepository>();
builder.Services.AddScoped<IFormRepository, FormRepository>();
builder.Services.AddScoped<IFormRuleRepository, FormRuleRepository>();
builder.Services.AddScoped<IRelationshipRepository, RelationshipRepository>();
builder.Services.AddScoped<IAdminRepository, AdminRepository>();

// Handlers
builder.Services.AddScoped<SignupCommandHandler>();
builder.Services.AddScoped<ForgotPasswordCommandHandler>();
builder.Services.AddScoped<ResetPasswordCommandHandler>();
builder.Services.AddScoped<AcceptInviteCommandHandler>();
builder.Services.AddScoped<LoginQueryHandler>();
builder.Services.AddScoped<GetMeQueryHandler>();
builder.Services.AddScoped<SelectTenantCommandHandler>();
builder.Services.AddScoped<RefreshTokenCommandHandler>();
builder.Services.AddScoped<CreateTenantCommandHandler>();
builder.Services.AddScoped<CreateAppCommandHandler>();
builder.Services.AddScoped<UpdateAppCommandHandler>();
builder.Services.AddScoped<ListAppUsersQueryHandler>();
builder.Services.AddScoped<AddAppUserCommandHandler>();
builder.Services.AddScoped<InviteAppUserCommandHandler>();
builder.Services.AddScoped<ChangeAppUserRoleCommandHandler>();
builder.Services.AddScoped<UpdateUserPickerVisibilityCommandHandler>();
builder.Services.AddScoped<RemoveAppUserCommandHandler>();
builder.Services.AddScoped<ListAppRolesQueryHandler>();
builder.Services.AddScoped<CreateAppRoleCommandHandler>();
builder.Services.AddScoped<DeleteAppRoleCommandHandler>();
builder.Services.AddScoped<UpdateAppRoleCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetTablePermissions.GetTablePermissionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.UpdateTablePermissions.UpdateTablePermissionsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetFieldPermissions.GetFieldPermissionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.UpdateFieldPermissions.UpdateFieldPermissionsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetRecordFilters.GetRecordFiltersQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Commands.UpdateRecordFilters.UpdateRecordFiltersCommandHandler>();
builder.Services.AddScoped<DeleteAppCommandHandler>();
builder.Services.AddScoped<GetAppQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetAppStorageUsage.GetAppStorageUsageQueryHandler>();
builder.Services.AddScoped<ListAppsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Apps.Queries.GetAppPermissions.GetAppPermissionsQueryHandler>();
builder.Services.AddScoped<ListAppVariablesQueryHandler>();
builder.Services.AddScoped<CreateAppVariableCommandHandler>();
builder.Services.AddScoped<UpdateAppVariableCommandHandler>();
builder.Services.AddScoped<DeleteAppVariableCommandHandler>();
builder.Services.AddScoped<CreateTableCommandHandler>();
builder.Services.AddScoped<UpdateTableCommandHandler>();
builder.Services.AddScoped<DeleteTableCommandHandler>();
builder.Services.AddScoped<GetTableQueryHandler>();
builder.Services.AddScoped<ListTablesQueryHandler>();
builder.Services.AddScoped<CreateFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Commands.BulkCreateFields.BulkCreateFieldsCommandHandler>();
builder.Services.AddScoped<UpdateFieldCommandHandler>();
builder.Services.AddScoped<DeleteFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Commands.BulkDeleteFields.BulkDeleteFieldsCommandHandler>();
builder.Services.AddScoped<ListFieldsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Queries.GetFieldUsage.GetFieldUsageQueryHandler>();
builder.Services.AddScoped<CreateRecordCommandHandler>();
builder.Services.AddScoped<UpdateRecordCommandHandler>();
builder.Services.AddScoped<DeleteRecordCommandHandler>();
builder.Services.AddScoped<BulkDeleteRecordsCommandHandler>();
builder.Services.AddScoped<ListRecordsQueryHandler>();
builder.Services.AddScoped<GetRecordQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Records.Queries.GetDistinctFieldValues.GetDistinctFieldValuesQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.CreateRelationship.CreateRelationshipCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.DeleteRelationship.DeleteRelationshipCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.AddLookupFields.AddLookupFieldsCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.AddSummaryField.AddSummaryFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Commands.RemoveRelationshipField.RemoveRelationshipFieldCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.RelationshipFieldFactory>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Queries.RelationshipQueriesHandler>();
builder.Services.AddScoped<PowerBase.Application.Relationships.Queries.GetParentOptionsQueryHandler>();
builder.Services.AddScoped<PowerBase.Application.Fields.Commands.SetKey.SetKeyCommandHandler>();
builder.Services.AddScoped<CreateReportCommandHandler>();
builder.Services.AddScoped<UpdateReportCommandHandler>();
builder.Services.AddScoped<DeleteReportCommandHandler>();
builder.Services.AddScoped<SetDefaultReportCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Reports.Commands.UpdateReportFormOverrides.UpdateReportFormOverridesCommandHandler>();
builder.Services.AddScoped<UpdateDefaultReportSettingsCommandHandler>();
builder.Services.AddScoped<GetReportQueryHandler>();
builder.Services.AddScoped<GetDefaultReportSettingsQueryHandler>();
builder.Services.AddScoped<ListReportsQueryHandler>();
builder.Services.AddScoped<ListReportsByTableQueryHandler>();
builder.Services.AddScoped<ResolveDefaultReportQueryHandler>();
builder.Services.AddScoped<RunReportQueryHandler>();
builder.Services.AddScoped<ExportReportQueryHandler>();
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
builder.Services.AddScoped<ListAuditLogsQueryHandler>();
builder.Services.AddScoped<ExportAuditLogsCsvQueryHandler>();
builder.Services.AddScoped<CreateFormCommandHandler>();
builder.Services.AddScoped<UpdateFormSettingsCommandHandler>();
builder.Services.AddScoped<SaveFormLayoutCommandHandler>();
builder.Services.AddScoped<DeleteFormCommandHandler>();
builder.Services.AddScoped<DuplicateFormCommandHandler>();
builder.Services.AddScoped<PowerBase.Application.Forms.Commands.SetDefaultForm.SetDefaultFormCommandHandler>();
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
builder.Services.AddScoped<ListFormRulesQueryHandler>();
builder.Services.AddScoped<GetFormRuleQueryHandler>();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors("Frontend");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

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
app.UseMiddleware<JwtMiddleware>();
app.MapControllers();

app.Run();

public partial class Program { }
