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
using PowerBase.Application.Apps.Commands.CreateAppVariable;
using PowerBase.Application.Apps.Commands.UpdateAppVariable;
using PowerBase.Application.Apps.Commands.DeleteAppVariable;
using PowerBase.Application.Apps.Queries.ListAppVariables;
using PowerBase.Application.Apps.Queries.GetApp;
using PowerBase.Application.Apps.Queries.ListAppRoles;
using PowerBase.Application.Apps.Queries.ListApps;
using PowerBase.Application.Apps.Queries.ListAppUsers;
using PowerBase.Application.Auth.Commands.AcceptInvite;
using PowerBase.Application.Auth.Commands.SelectTenant;
using PowerBase.Application.Auth.Commands.Signup;
using PowerBase.Application.Auth.Queries.GetMe;
using PowerBase.Application.Auth.Queries.Login;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tenants.Commands.CreateTenant;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Commands.DeleteField;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Queries.ListFields;
using PowerBase.Application.Records.Commands.CreateRecord;
using PowerBase.Application.Records.Commands.DeleteRecord;
using PowerBase.Application.Records.Commands.UpdateRecord;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Application.Records.Queries.ListRecords;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Commands.DeleteReport;
using PowerBase.Application.Reports.Commands.SetDefaultReport;
using PowerBase.Application.Reports.Commands.UpdateReport;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Application.Reports.Queries.ListReports;
using PowerBase.Application.Reports.Queries.ListReportsByTable;
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
using PowerBase.Application.Users.Commands.ChangeUserRole;
using PowerBase.Application.Users.Commands.InviteUser;
using PowerBase.Application.Users.Commands.RemoveUser;
using PowerBase.Application.Users.Queries.ListUsers;
using PowerBase.Infrastructure.Persistence;
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
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddScoped<UnitOfWork>();
builder.Services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<UnitOfWork>());

// Services
builder.Services.AddScoped<QueryContext>();
builder.Services.AddScoped<IQueryContext>(sp => sp.GetRequiredService<QueryContext>());
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IAppAccessService, AppAccessService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<ISchemaEngineService, SchemaEngineService>();

// Repositories
builder.Services.AddScoped<IAppRepository, AppRepository>();
builder.Services.AddScoped<IAppVariableRepository, AppVariableRepository>();
builder.Services.AddScoped<IAppRoleRepository, AppRoleRepository>();
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

// Handlers
builder.Services.AddScoped<SignupCommandHandler>();
builder.Services.AddScoped<AcceptInviteCommandHandler>();
builder.Services.AddScoped<LoginQueryHandler>();
builder.Services.AddScoped<GetMeQueryHandler>();
builder.Services.AddScoped<SelectTenantCommandHandler>();
builder.Services.AddScoped<CreateTenantCommandHandler>();
builder.Services.AddScoped<CreateAppCommandHandler>();
builder.Services.AddScoped<UpdateAppCommandHandler>();
builder.Services.AddScoped<ListAppUsersQueryHandler>();
builder.Services.AddScoped<AddAppUserCommandHandler>();
builder.Services.AddScoped<ChangeAppUserRoleCommandHandler>();
builder.Services.AddScoped<RemoveAppUserCommandHandler>();
builder.Services.AddScoped<ListAppRolesQueryHandler>();
builder.Services.AddScoped<CreateAppRoleCommandHandler>();
builder.Services.AddScoped<DeleteAppRoleCommandHandler>();
builder.Services.AddScoped<UpdateAppRoleCommandHandler>();
builder.Services.AddScoped<DeleteAppCommandHandler>();
builder.Services.AddScoped<GetAppQueryHandler>();
builder.Services.AddScoped<ListAppsQueryHandler>();
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
builder.Services.AddScoped<UpdateFieldCommandHandler>();
builder.Services.AddScoped<DeleteFieldCommandHandler>();
builder.Services.AddScoped<ListFieldsQueryHandler>();
builder.Services.AddScoped<CreateRecordCommandHandler>();
builder.Services.AddScoped<UpdateRecordCommandHandler>();
builder.Services.AddScoped<DeleteRecordCommandHandler>();
builder.Services.AddScoped<ListRecordsQueryHandler>();
builder.Services.AddScoped<GetRecordQueryHandler>();
builder.Services.AddScoped<CreateReportCommandHandler>();
builder.Services.AddScoped<UpdateReportCommandHandler>();
builder.Services.AddScoped<DeleteReportCommandHandler>();
builder.Services.AddScoped<SetDefaultReportCommandHandler>();
builder.Services.AddScoped<GetReportQueryHandler>();
builder.Services.AddScoped<ListReportsQueryHandler>();
builder.Services.AddScoped<ListReportsByTableQueryHandler>();
builder.Services.AddScoped<RunReportQueryHandler>();
builder.Services.AddScoped<ListUsersQueryHandler>();
builder.Services.AddScoped<InviteUserCommandHandler>();
builder.Services.AddScoped<ChangeUserRoleCommandHandler>();
builder.Services.AddScoped<RemoveUserCommandHandler>();
builder.Services.AddScoped<ListRolesQueryHandler>();
builder.Services.AddScoped<CreateRoleCommandHandler>();
builder.Services.AddScoped<UpdateRoleCommandHandler>();
builder.Services.AddScoped<DeleteRoleCommandHandler>();
builder.Services.AddScoped<GetRolePermissionsQueryHandler>();
builder.Services.AddScoped<UpdateRolePermissionsCommandHandler>();
builder.Services.AddScoped<ListPermissionsQueryHandler>();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors("Frontend");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseMiddleware<QueryContextMiddleware>();
app.UseMiddleware<JwtMiddleware>();
app.MapControllers();

app.Run();

public partial class Program { }
