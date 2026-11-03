using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>Copy App – Schema Only. All the copying happens in meta.usp_CopyAppSchema (migration 090); this class runs it
/// in a transaction and adds the one thing SQL cannot do: the new app's data key.</summary>
public class AppCopyRepository : TenantRepositoryBase, IAppCopyRepository
{
    private const string CopyAppSchemaProcedure = "meta.usp_CopyAppSchema";

    private const string GetAppSql = """
        SELECT PublicId, SecurityOptions
        FROM meta.App
        WHERE Id = @appId
        """;

    private const string SetSecurityOptionsSql = """
        UPDATE meta.App SET SecurityOptions = @securityOptions
        WHERE Id = @appId
        """;

    // THROW numbers used by the procedure.
    private const int SQL_APP_NOT_FOUND = 50404;
    private const int SQL_NAME_TAKEN = 50409;

    // A big app is a few seconds; this only guards against a hung copy.
    private const int COMMAND_TIMEOUT_SECONDS = 300;

    // The procedure's progress messages: "copy-progress:<percent>:<stage>" (see migration 090).
    private const string PROGRESS_PREFIX = "copy-progress:";
    private const int PROGRESS_SAVING = 99;
    private const string PROGRESS_SAVING_STAGE = "Saving the copy";

    private readonly IEncryptionService _encryptionService;
    private readonly IAppCopyProgressStore _progressStore;

    public AppCopyRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IEncryptionService encryptionService,
        IAppCopyProgressStore progressStore)
        : base(connectionFactory, queryContext)
    {
        _encryptionService = encryptionService;
        _progressStore = progressStore;
    }

    public async Task<AppSchemaCopyResult> CopySchemaAsync(AppSchemaCopyRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (request.ProgressId is { } progressId)
            connection.InfoMessage += (_, e) => ReportProgress(progressId, request.UserId, e);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var parameters = new DynamicParameters();
            parameters.Add("@SourceAppId", request.SourceAppId);
            parameters.Add("@NewName", request.NewName);
            parameters.Add("@NewDescription", request.NewDescription);
            parameters.Add("@UserId", request.UserId);
            parameters.Add("@UserPublicId", request.UserPublicId);
            parameters.Add("@UserName", request.UserName);
            parameters.Add("@UserEmail", request.UserEmail);
            parameters.Add("@CopyUsers", request.CopyUsers);
            parameters.Add("@NewAppId", dbType: DbType.Int64, direction: ParameterDirection.Output);

            List<AppCopyWarning> warnings;
            List<AppCopyStageTiming> stages;
            using (var grid = await connection.QueryMultipleAsync(new CommandDefinition(
                       CopyAppSchemaProcedure, parameters, transaction, COMMAND_TIMEOUT_SECONDS,
                       CommandType.StoredProcedure, cancellationToken: ct)))
            {
                warnings = (await grid.ReadAsync<AppCopyWarning>()).ToList();
                stages = (await grid.ReadAsync<AppCopyStageTiming>()).ToList();
            }

            // Output parameters are only filled once every result set has been read.
            var newAppId = parameters.Get<long>("@NewAppId");
            if (request.ProgressId is { } savingId)
                _progressStore.Report(savingId, request.UserId, PROGRESS_SAVING, PROGRESS_SAVING_STAGE);

            var app = await connection.QuerySingleAsync<(Guid PublicId, string? SecurityOptions)>(
                new CommandDefinition(GetAppSql, new { appId = newAppId }, transaction, cancellationToken: ct));

            if (request.IsEncrypted)
            {
                // The procedure dropped the original's wrapped key; the copy gets its own, as a new app does in AppRepository.
                var security = string.IsNullOrEmpty(app.SecurityOptions) || !app.SecurityOptions.TrimStart().StartsWith('{')
                    ? new AppSecurityOptionsSettings()
                    : JsonSerializer.Deserialize<AppSecurityOptionsSettings>(app.SecurityOptions, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? new AppSecurityOptionsSettings();
                security.WrappedDek = await _encryptionService.GenerateAndWrapDekAsync(QueryContext.TenantId, newAppId, ct);

                await connection.ExecuteAsync(new CommandDefinition(
                    SetSecurityOptionsSql, new { appId = newAppId, securityOptions = JsonSerializer.Serialize(security) }, transaction, cancellationToken: ct));
            }

            await transaction.CommitAsync(ct);
            if (request.ProgressId is { } doneId)
                _progressStore.Complete(doneId, request.UserId, failed: false);
            return new AppSchemaCopyResult(newAppId, app.PublicId, warnings, stages);
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(transaction);
            if (request.ProgressId is { } failedId)
                _progressStore.Complete(failedId, request.UserId, failed: true);
            if (ex is SqlException { Number: SQL_NAME_TAKEN })
                throw new DuplicateException("App", "name", request.NewName);
            if (ex is SqlException { Number: SQL_APP_NOT_FOUND })
                throw new NotFoundException("App", request.SourceAppId);
            throw;
        }
    }

    /// <summary>Passes the procedure's progress messages on to the store; every other informational message is ignored.</summary>
    private void ReportProgress(Guid progressId, long userId, SqlInfoMessageEventArgs e)
    {
        foreach (SqlError message in e.Errors)
        {
            if (TryParseProgress(message.Message, out var percent, out var stage))
                _progressStore.Report(progressId, userId, percent, stage);
        }
    }

    /// <summary>"copy-progress:52:Copying forms and rules" -> (52, "Copying forms and rules").</summary>
    internal static bool TryParseProgress(string? message, out int percent, out string stage)
    {
        percent = 0;
        stage = string.Empty;
        if (message is null || !message.StartsWith(PROGRESS_PREFIX, StringComparison.Ordinal)) return false;
        var rest = message.AsSpan(PROGRESS_PREFIX.Length);
        var colon = rest.IndexOf(':');
        if (colon <= 0 || !int.TryParse(rest[..colon], out percent)) return false;
        stage = rest[(colon + 1)..].ToString();
        return true;
    }

    /// <summary>The procedure runs with XACT_ABORT ON, so on an error SQL Server has often rolled back already.</summary>
    private static async Task RollbackQuietlyAsync(SqlTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); }
        catch (InvalidOperationException) { /* already rolled back */ }
        catch (SqlException) { /* already rolled back */ }
    }
}
