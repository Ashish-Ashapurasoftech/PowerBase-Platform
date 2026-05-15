using PowerBase.API.Middleware;
using PowerBase.Application.Apps.Commands.CreateApp;
using PowerBase.Application.Apps.Commands.DeleteApp;
using PowerBase.Application.Apps.Queries.GetApp;
using PowerBase.Application.Apps.Queries.ListApps;
using PowerBase.Application.Auth.Commands.Signup;
using PowerBase.Application.Auth.Queries.GetMe;
using PowerBase.Application.Auth.Queries.Login;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Queries.ListFields;
using PowerBase.Application.Records.Commands.CreateRecord;
using PowerBase.Application.Records.Commands.DeleteRecord;
using PowerBase.Application.Records.Commands.UpdateRecord;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Application.Records.Queries.ListRecords;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Application.Reports.Queries.ListReports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Application.Tables.Commands.CreateTable;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Application.Tables.Queries.GetTable;
using PowerBase.Application.Tables.Queries.ListTables;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.Services;
using PowerBase.Infrastructure.UOW;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

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
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<ISchemaEngineService, SchemaEngineService>();

// Repositories
builder.Services.AddScoped<IAppRepository, AppRepository>();
builder.Services.AddScoped<IAppTableRepository, AppTableRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<ISystemRoleRepository, SystemRoleRepository>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<IAppFieldRepository, AppFieldRepository>();
builder.Services.AddScoped<IFieldTypeRepository, FieldTypeRepository>();
builder.Services.AddScoped<IRecordRepository, RecordRepository>();
builder.Services.AddScoped<IReportRepository, ReportRepository>();

// Handlers
builder.Services.AddScoped<SignupCommandHandler>();
builder.Services.AddScoped<LoginQueryHandler>();
builder.Services.AddScoped<GetMeQueryHandler>();
builder.Services.AddScoped<CreateAppCommandHandler>();
builder.Services.AddScoped<DeleteAppCommandHandler>();
builder.Services.AddScoped<GetAppQueryHandler>();
builder.Services.AddScoped<ListAppsQueryHandler>();
builder.Services.AddScoped<CreateTableCommandHandler>();
builder.Services.AddScoped<DeleteTableCommandHandler>();
builder.Services.AddScoped<GetTableQueryHandler>();
builder.Services.AddScoped<ListTablesQueryHandler>();
builder.Services.AddScoped<CreateFieldCommandHandler>();
builder.Services.AddScoped<ListFieldsQueryHandler>();
builder.Services.AddScoped<CreateRecordCommandHandler>();
builder.Services.AddScoped<UpdateRecordCommandHandler>();
builder.Services.AddScoped<DeleteRecordCommandHandler>();
builder.Services.AddScoped<ListRecordsQueryHandler>();
builder.Services.AddScoped<GetRecordQueryHandler>();
builder.Services.AddScoped<CreateReportCommandHandler>();
builder.Services.AddScoped<GetReportQueryHandler>();
builder.Services.AddScoped<ListReportsQueryHandler>();
builder.Services.AddScoped<RunReportQueryHandler>();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

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
