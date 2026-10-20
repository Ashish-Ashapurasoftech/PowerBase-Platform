using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppImportJobRepository : TenantRepositoryBase, IAppImportJobRepository
{
    private const string Columns = @"Id, PublicId, UserId, FileName, FileSizeBytes, Status, Stage, PercentComplete, AppPublicId, AppName,
        ResultJson, ErrorMessage, MappingJson, FrontendBaseUrl, EmailSentOn, EmailError, IsDismissed, CreatedOn, StartedOn, CompletedOn";

    private const string InsertSql = @"
        INSERT INTO meta.AppImportJob (UserId, FileName, FileSizeBytes, Status, Stage, PercentComplete, Content, MappingJson, FrontendBaseUrl)
        OUTPUT INSERTED.Id, INSERTED.PublicId, INSERTED.CreatedOn
        VALUES (@UserId, @FileName, @FileSizeBytes, @Status, @Stage, 0, @Content, @MappingJson, @FrontendBaseUrl)";

    private const string GetSql = @"SELECT " + Columns + @" FROM meta.AppImportJob WHERE PublicId = @publicId";

    private const string GetWithContentSql = @"SELECT " + Columns + @", Content FROM meta.AppImportJob WHERE PublicId = @publicId";

    private const string ListSql = @"SELECT " + Columns + @" FROM meta.AppImportJob
        WHERE UserId = @userId AND IsDismissed = 0 ORDER BY CreatedOn DESC OFFSET 0 ROWS FETCH NEXT 50 ROWS ONLY";

    public AppImportJobRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<AppImportJob> CreateAsync(AppImportJob job, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        var row = await c.QuerySingleAsync<(long Id, Guid PublicId, DateTime CreatedOn)>(new CommandDefinition(InsertSql, job, cancellationToken: ct));
        job.Id = row.Id;
        job.PublicId = row.PublicId;
        job.CreatedOn = row.CreatedOn;
        return job;
    }

    public async Task<AppImportJob?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.QuerySingleOrDefaultAsync<AppImportJob>(new CommandDefinition(GetSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<AppImportJob?> GetWithContentAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.QuerySingleOrDefaultAsync<AppImportJob>(new CommandDefinition(GetWithContentSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AppImportJob>> ListForUserAsync(long userId, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return (await c.QueryAsync<AppImportJob>(new CommandDefinition(ListSql, new { userId }, cancellationToken: ct))).AsList();
    }

    public Task MarkRunningAsync(long id, CancellationToken ct = default) => ExecAsync(
        "UPDATE meta.AppImportJob SET Status = 'Running', StartedOn = SYSUTCDATETIME(), Stage = N'Starting', PercentComplete = 1 WHERE Id = @id", new { id }, ct);

    public Task UpdateProgressAsync(long id, string stage, int percent, CancellationToken ct = default) => ExecAsync(
        "UPDATE meta.AppImportJob SET Stage = @stage, PercentComplete = @percent WHERE Id = @id AND Status = 'Running'", new { id, stage, percent }, ct);

    public Task CompleteAsync(long id, Guid appPublicId, string appName, string resultJson, CancellationToken ct = default) => ExecAsync(
        @"UPDATE meta.AppImportJob SET Status = 'Completed', Stage = N'Done', PercentComplete = 100, AppPublicId = @appPublicId, AppName = @appName,
          ResultJson = @resultJson, Content = NULL, CompletedOn = SYSUTCDATETIME() WHERE Id = @id", new { id, appPublicId, appName, resultJson }, ct);

    public Task FailAsync(long id, string error, CancellationToken ct = default) => ExecAsync(
        @"UPDATE meta.AppImportJob SET Status = 'Failed', Stage = N'Failed', ErrorMessage = @error, Content = NULL, CompletedOn = SYSUTCDATETIME() WHERE Id = @id",
        new { id, error = error.Length > 2000 ? error[..2000] : error }, ct);

    public Task SetEmailResultAsync(long id, bool sent, string? error, CancellationToken ct = default) => ExecAsync(
        "UPDATE meta.AppImportJob SET EmailSentOn = CASE WHEN @sent = 1 THEN SYSUTCDATETIME() ELSE NULL END, EmailError = @error WHERE Id = @id",
        new { id, sent, error = error is { Length: > 500 } ? error[..500] : error }, ct);

    public Task DismissAsync(Guid publicId, long userId, CancellationToken ct = default) => ExecAsync(
        "UPDATE meta.AppImportJob SET IsDismissed = 1 WHERE PublicId = @publicId AND UserId = @userId AND Status IN ('Completed','Failed')", new { publicId, userId }, ct);

    public Task FailInterruptedAsync(long userId, DateTime startedBefore, CancellationToken ct = default) => ExecAsync(
        @"UPDATE meta.AppImportJob SET Status = 'Failed', Stage = N'Failed', Content = NULL, CompletedOn = SYSUTCDATETIME(),
          ErrorMessage = N'The import was interrupted because the server restarted. Please upload the file again.'
          WHERE UserId = @userId AND Status IN ('Queued','Running') AND CreatedOn < @startedBefore", new { userId, startedBefore }, ct);

    private async Task ExecAsync(string sql, object args, CancellationToken ct)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }
}

public class EmailTemplateRepository : TenantRepositoryBase, IEmailTemplateRepository
{
    public EmailTemplateRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<EmailTemplate?> GetAsync(string templateKey, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        return await c.QuerySingleOrDefaultAsync<EmailTemplate>(new CommandDefinition(
            "SELECT Id, TemplateKey, Subject, Body, IsEnabled, ModifiedOn, ModifiedBy FROM meta.EmailTemplate WHERE TemplateKey = @templateKey",
            new { templateKey }, cancellationToken: ct));
    }

    public async Task UpsertAsync(string templateKey, string subject, string body, bool isEnabled, long? modifiedBy, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE meta.EmailTemplate SET Subject = @subject, Body = @body, IsEnabled = @isEnabled, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE TemplateKey = @templateKey;
            IF @@ROWCOUNT = 0
                INSERT INTO meta.EmailTemplate (TemplateKey, Subject, Body, IsEnabled, ModifiedBy) VALUES (@templateKey, @subject, @body, @isEnabled, @modifiedBy);",
            new { templateKey, subject, body, isEnabled, modifiedBy }, cancellationToken: ct));
    }

    public async Task DeleteAsync(string templateKey, CancellationToken ct = default)
    {
        await using var c = await ConnectionFactory.CreateAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("DELETE FROM meta.EmailTemplate WHERE TemplateKey = @templateKey", new { templateKey }, cancellationToken: ct));
    }
}
