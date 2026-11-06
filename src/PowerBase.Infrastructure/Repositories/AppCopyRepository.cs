using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Apps.Commands.CopyApp;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>Copy App. All the copying of records happens in SQL procedures: meta.usp_CopyAppSchema (migration 090) for the
/// structure, and for a copy with data the procedures of migrations 091 and 093. This class runs them and adds what SQL
/// cannot do: the new app's data key, and the copy's own file attachments (every file and revision copied in storage).</summary>
public class AppCopyRepository : TenantRepositoryBase, IAppCopyRepository
{
    private const string CopyAppSchemaProcedure = "meta.usp_CopyAppSchema";
    private const string CopyAppDataProcedure = "meta.usp_CopyAppData";
    private const string FinishAppCopyProcedure = "meta.usp_FinishAppCopy";
    private const string DiscardAppCopyProcedure = "meta.usp_DiscardAppCopy";
    private const string DiscardStaleAppCopiesProcedure = "meta.usp_DiscardStaleAppCopies";

    private const string GetAppSql = """
        SELECT PublicId, SecurityOptions
        FROM meta.App
        WHERE Id = @appId
        """;

    private const string GetSecurityOptionsSql = """
        SELECT SecurityOptions
        FROM meta.App
        WHERE Id = @appId
        """;

    private const string SetSecurityOptionsSql = """
        UPDATE meta.App SET SecurityOptions = @securityOptions
        WHERE Id = @appId
        """;

    // A copy with data stays out of everyone's apps list until its records are in (usp_FinishAppCopy makes it Active).
    private const string SetDraftSql = """
        UPDATE meta.App SET Status = 'Draft'
        WHERE Id = @appId
        """;

    private const string InsertCopyRunSql = """
        INSERT INTO meta.AppCopyRun (ProgressId, SourceAppId, NewAppId, NewAppPublicId, StartedBy, IncludeFiles,
                                     ProgressPercent, Stage)
        OUTPUT inserted.Id
        VALUES (@progressId, @sourceAppId, @newAppId, @newAppPublicId, @userId, @includeFiles, @percent, N'Preparing record ids')
        """;

    // READPAST: while the first step has not committed, its run row is skipped instead of making the poll wait.
    private const string GetCopyRunProgressSql = """
        SELECT ProgressPercent, Stage, Status, NewAppPublicId, RowsCopied, WarningsJson, Error
        FROM meta.AppCopyRun WITH (READPAST)
        WHERE ProgressId = @progressId AND StartedBy = @userId
        """;

    // The app list's copies: running ones, and finished or failed ones of the last days nobody dismissed. READPAST, so a
    // copy whose first step is still open (its run and Draft app rows locked) is skipped instead of making the list wait.
    private const string ListCopyRunsSql = """
        SELECT TOP (20) r.NewAppPublicId AS Id, a.Name, s.PublicId AS SourceAppPublicId, s.Name AS SourceAppName, r.Status,
               r.ProgressPercent AS [Percent], r.Stage, r.RowsCopied AS RecordsCopied, r.TotalRows AS TotalRecords, r.IncludeFiles,
               CAST(DATEDIFF(SECOND, r.StartedOn, ISNULL(r.FinishedOn, SYSUTCDATETIME())) AS BIGINT) AS ElapsedSeconds, r.Error
        FROM meta.AppCopyRun r WITH (READPAST)
        JOIN meta.App a WITH (READPAST) ON a.Id = r.NewAppId
        JOIN meta.App s WITH (READPAST) ON s.Id = r.SourceAppId
        WHERE r.StartedBy = @userId AND r.IsDismissed = 0
          AND (r.Status = 'Running' OR r.FinishedOn > DATEADD(DAY, -@keepDays, SYSUTCDATETIME()))
        ORDER BY r.Id DESC
        """;

    private const string DismissCopyRunSql = """
        UPDATE meta.AppCopyRun SET IsDismissed = 1
        WHERE NewAppPublicId = @copyAppPublicId AND StartedBy = @userId AND Status <> 'Running'
        """;

    // The API process running a copy shows it is alive, whatever stage the copy is in (a chunk, the file paths).
    private const string HeartbeatSql = """
        UPDATE meta.AppCopyRun SET LastProgressOn = SYSUTCDATETIME()
        WHERE Id = @runId AND Status = 'Running'
        """;

    private const int HEARTBEAT_SECONDS = 15;
    // A running copy whose heartbeat stopped this long ago lost its API process (stopped or restarted): it is removed and
    // shows as failed. Several missed beats, so a slow moment of the database does not end a live copy.
    private const int STALE_MINUTES = 2;

    // Finished and failed copies stay in the app list this long unless dismissed.
    private const int COPY_RUN_KEEP_DAYS = 7;

    // The copy's File columns (the copy keeps the original's table and field names and physical columns).
    private const string ListFileColumnsSql = """
        SELECT t.Id AS TableId, t.Name AS TableName, f.PhysicalColumnName AS ColumnName, MIN(f.Name) AS FieldName,
               CAST(MAX(CAST(f.IsEncrypted AS INT)) AS BIT) AS IsEncrypted
        FROM meta.AppField f
        JOIN meta.AppTable t ON t.Id = f.AppTableId
        JOIN core.FieldType ft ON ft.Id = f.FieldTypeId
        WHERE t.AppId = @appId AND t.IsDeleted = 0 AND f.IsDeleted = 0 AND ft.Code = 'File' AND f.PhysicalColumnName IS NOT NULL
        GROUP BY t.Id, t.Name, f.PhysicalColumnName
        ORDER BY t.Id, f.PhysicalColumnName
        """;

    private const string FileProgressSql = """
        UPDATE meta.AppCopyRun SET ProgressPercent = @percent, Stage = @stage, LastProgressOn = SYSUTCDATETIME()
        WHERE Id = @runId AND Status = 'Running'
        """;

    // THROW numbers used by the procedure.
    private const int SQL_APP_NOT_FOUND = 50404;
    private const int SQL_NAME_TAKEN = 50409;

    // A big app is a few seconds; this only guards against a hung copy.
    private const int COMMAND_TIMEOUT_SECONDS = 300;
    // The records of a big app can take many minutes; the procedure moves the run's progress on with every chunk.
    private const int NO_COMMAND_TIMEOUT = 0;
    private const int FILE_VALUES_PAGE_SIZE = 1000;
    private const int MAX_ERROR_LENGTH = 2000;

    // The procedure's progress messages: "copy-progress:<percent>:<stage>" (see migration 090).
    private const string PROGRESS_PREFIX = "copy-progress:";
    private const int PROGRESS_SAVING = 99;
    private const string PROGRESS_SAVING_STAGE = "Saving the copy";
    // In a copy with data the structure is the first 3% of the bar; meta.AppCopyRun takes it from there. The records end
    // at 97%, or at 80% when files are copied too, which take 80–97%.
    private const int DATA_COPY_SCHEMA_PERCENT = 3;
    private const int DATA_COPY_FILES_PERCENT = 80;
    private const int DATA_COPY_END_PERCENT = 97;
    // Missing files listed one by one in the copy's warnings; the rest are counted in one more warning.
    private const int MAX_FILE_WARNINGS = 50;
    private const string FILE_NOT_FOUND_WARNING = "FILE_NOT_FOUND";

    private const string RUN_RUNNING = "Running";
    private const string RUN_DONE = "Done";
    private const string RUN_FAILED = "Failed";

    private static readonly Regex DataColumnName = new("^f_[0-9]+$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IEncryptionService _encryptionService;
    private readonly IAppCopyProgressStore _progressStore;
    private readonly IFileStorageService _fileStorage;

    public AppCopyRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IEncryptionService encryptionService,
        IAppCopyProgressStore progressStore, IFileStorageService fileStorage)
        : base(connectionFactory, queryContext)
    {
        _encryptionService = encryptionService;
        _progressStore = progressStore;
        _fileStorage = fileStorage;
    }

    public async Task<AppSchemaCopyResult> CopySchemaAsync(AppSchemaCopyRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (request.ProgressId is { } progressId)
            connection.InfoMessage += (_, e) => ReportProgress(progressId, request.UserId, e, scaleTo: 100);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var schema = await RunSchemaProcedureAsync(connection, transaction, request, ct);
            if (request.ProgressId is { } savingId)
                _progressStore.Report(savingId, request.UserId, PROGRESS_SAVING, PROGRESS_SAVING_STAGE);

            if (request.IsEncrypted)
            {
                // The procedure dropped the original's wrapped key; the copy gets its own, as a new app does in AppRepository.
                var security = ParseSecurityOptions(schema.SecurityOptions);
                security.WrappedDek = await _encryptionService.GenerateAndWrapDekAsync(QueryContext.TenantId, schema.NewAppId, ct);
                await SetSecurityOptionsAsync(connection, transaction, schema.NewAppId, security, ct);
            }

            await transaction.CommitAsync(ct);
            if (request.ProgressId is { } doneId)
                _progressStore.Complete(doneId, request.UserId, failed: false);
            return new AppSchemaCopyResult(schema.NewAppId, schema.NewAppPublicId, schema.Warnings, schema.Stages);
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(transaction);
            if (request.ProgressId is { } failedId)
                _progressStore.Complete(failedId, request.UserId, failed: true);
            var translated = TranslateSchemaError(ex, request);
            if (translated is not null) throw translated;
            throw;
        }
    }

    public async Task<AppDataCopyResult> CopyWithDataAsync(AppDataCopyRequest request, CancellationToken ct = default)
    {
        var started = await StartDataCopyAsync(request, ct);

        // From here on the copy does not depend on the request: a closed tab or a response cut by the hosting timeout does
        // not stop it, and the dialog follows it in meta.AppCopyRun. A copy whose API process stops has no catch to run;
        // its heartbeat stops instead, so usp_DiscardStaleAppCopies finds it within minutes.
        using var stopHeartbeat = new CancellationTokenSource();
        var heartbeat = KeepRunAliveAsync(started.RunId, stopHeartbeat.Token);
        try
        {
            await using var connection = await OpenConnectionAsync(CancellationToken.None);
            var fileColumns = request.IncludeFiles
                ? (await connection.QueryAsync<FileColumn>(new CommandDefinition(
                      ListFileColumnsSql, new { appId = started.NewAppId }, commandTimeout: COMMAND_TIMEOUT_SECONDS)))
                  .Where(c => DataColumnName.IsMatch(c.ColumnName)).ToList()
                : [];

            // File values are copied as they are (or NULL when files are left out); the Files stage then gives the copy its
            // own files.
            var tables = (await connection.QueryAsync<CopiedTable>(new CommandDefinition(
                CopyAppDataProcedure,
                new { RunId = started.RunId, RowsEndPercent = fileColumns.Count > 0 ? DATA_COPY_FILES_PERCENT : DATA_COPY_END_PERCENT },
                commandTimeout: NO_COMMAND_TIMEOUT, commandType: CommandType.StoredProcedure))).ToList();

            if (fileColumns.Count > 0)
                started.Warnings.AddRange(await CopyFilesAsync(connection, started, fileColumns));

            await connection.ExecuteAsync(new CommandDefinition(
                FinishAppCopyProcedure, new { RunId = started.RunId, WarningsJson = JsonSerializer.Serialize(started.Warnings) },
                commandTimeout: COMMAND_TIMEOUT_SECONDS, commandType: CommandType.StoredProcedure));

            return new AppDataCopyResult(started.NewAppId, started.NewAppPublicId, started.Warnings,
                tables.Sum(t => t.RowTotal), tables.Where(t => t.RowTotal > 0).Select(t => t.NewTableId).ToList());
        }
        catch (Exception ex)
        {
            await DiscardQuietlyAsync(started.RunId, ex.Message, request.Schema.UserId);
            await DeleteCopiedFilesQuietlyAsync(started.NewAppPublicId);
            throw;
        }
        finally
        {
            stopHeartbeat.Cancel();
            await heartbeat;
        }
    }

    public async Task DiscardStaleDataCopiesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        // The procedure returns the PublicIds of the copies it removed (migration 093); their files go too.
        var discarded = (await connection.QueryAsync<Guid>(new CommandDefinition(
            DiscardStaleAppCopiesProcedure, new { StaleMinutes = STALE_MINUTES }, commandTimeout: COMMAND_TIMEOUT_SECONDS,
            commandType: CommandType.StoredProcedure, cancellationToken: ct))).ToList();
        foreach (var appPublicId in discarded)
            await DeleteCopiedFilesQuietlyAsync(appPublicId);
    }

    public async Task<AppCopyProgress?> GetDataCopyProgressAsync(Guid progressId, long userId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var run = await connection.QuerySingleOrDefaultAsync<CopyRunProgress>(new CommandDefinition(
            GetCopyRunProgressSql, new { progressId, userId }, cancellationToken: ct));
        if (run is null) return null;

        var warnings = string.IsNullOrEmpty(run.WarningsJson)
            ? null
            : JsonSerializer.Deserialize<List<AppCopyWarning>>(run.WarningsJson, JsonOptions);
        return new AppCopyProgress(
            run.ProgressPercent, run.Stage, IsDone: run.Status != RUN_RUNNING, IsFailed: run.Status == RUN_FAILED,
            NewAppPublicId: run.Status == RUN_DONE ? run.NewAppPublicId : null, RecordsCopied: run.RowsCopied,
            Warnings: warnings, Error: run.Error);
    }

    public async Task<IReadOnlyList<AppCopyRunSummary>> ListDataCopiesAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        return (await connection.QueryAsync<AppCopyRunSummary>(new CommandDefinition(
            ListCopyRunsSql, new { userId, keepDays = COPY_RUN_KEEP_DAYS }, cancellationToken: ct))).AsList();
    }

    public async Task DismissDataCopyAsync(Guid copyAppPublicId, long userId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(DismissCopyRunSql, new { copyAppPublicId, userId }, cancellationToken: ct));
    }

    /// <summary>First step of a copy with data, in one transaction that stops when <paramref name="ct"/> is cancelled (the
    /// CopyApp command passes none, so a closed tab does not undo it): the structure into a Draft app, the original's data
    /// key wrapped for the copy, and the run row that tracks the rest.</summary>
    private async Task<StartedDataCopy> StartDataCopyAsync(AppDataCopyRequest request, CancellationToken ct)
    {
        var schemaRequest = request.Schema;
        await using var connection = await OpenConnectionAsync(ct);
        if (schemaRequest.ProgressId is { } progressId)
            connection.InfoMessage += (_, e) => ReportProgress(progressId, schemaRequest.UserId, e, scaleTo: DATA_COPY_SCHEMA_PERCENT);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var schema = await RunSchemaProcedureAsync(connection, transaction, schemaRequest, ct);

            if (schemaRequest.IsEncrypted)
            {
                // The SAME data key as the original, wrapped for the copy: the copied ciphertext stays readable and is
                // never decrypted. An encrypted original without a key has nothing encrypted to read; the copy gets its own.
                var sourceSecurity = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                    GetSecurityOptionsSql, new { appId = schemaRequest.SourceAppId }, transaction, cancellationToken: ct));
                var sourceWrappedDek = ParseSecurityOptions(sourceSecurity).WrappedDek;
                var security = ParseSecurityOptions(schema.SecurityOptions);
                security.WrappedDek = string.IsNullOrEmpty(sourceWrappedDek)
                    ? await _encryptionService.GenerateAndWrapDekAsync(QueryContext.TenantId, schema.NewAppId, ct)
                    : await _encryptionService.RewrapDekAsync(sourceWrappedDek, QueryContext.TenantId, schemaRequest.SourceAppId, schema.NewAppId, ct);
                await SetSecurityOptionsAsync(connection, transaction, schema.NewAppId, security, ct);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                SetDraftSql, new { appId = schema.NewAppId }, transaction, cancellationToken: ct));
            var runId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(InsertCopyRunSql, new
            {
                progressId = schemaRequest.ProgressId,
                sourceAppId = schemaRequest.SourceAppId,
                newAppId = schema.NewAppId,
                newAppPublicId = schema.NewAppPublicId,
                userId = schemaRequest.UserId,
                includeFiles = request.IncludeFiles,
                percent = DATA_COPY_SCHEMA_PERCENT
            }, transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);
            return new StartedDataCopy(runId, schema.NewAppId, schema.NewAppPublicId, schema.Warnings);
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(transaction);
            if (schemaRequest.ProgressId is { } failedId)
                _progressStore.Complete(failedId, schemaRequest.UserId, failed: true);
            var translated = TranslateSchemaError(ex, schemaRequest);
            if (translated is not null) throw translated;
            throw;
        }
    }

    private static async Task<SchemaCopy> RunSchemaProcedureAsync(SqlConnection connection, SqlTransaction transaction,
        AppSchemaCopyRequest request, CancellationToken ct)
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
        var app = await connection.QuerySingleAsync<(Guid PublicId, string? SecurityOptions)>(
            new CommandDefinition(GetAppSql, new { appId = newAppId }, transaction, cancellationToken: ct));
        return new SchemaCopy(newAppId, app.PublicId, app.SecurityOptions, warnings, stages);
    }

    /// <summary>The Files stage of a copy with data "including file attachments": the records are in, their File values
    /// still point at the original's files. A page at a time, every file and revision is copied in storage
    /// (<see cref="AppCopyFileCopier"/>) and the value is written back with the new paths; encrypted values are decrypted,
    /// changed and encrypted again with the copy's key (the same data key, D4). Returns the warnings for missing files.</summary>
    private async Task<List<AppCopyWarning>> CopyFilesAsync(SqlConnection connection, StartedDataCopy started,
        IReadOnlyList<FileColumn> columns)
    {
        var copier = new AppCopyFileCopier(_fileStorage, started.NewAppPublicId);
        string? wrappedDek = null;
        if (columns.Any(c => c.IsEncrypted))
        {
            var security = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                GetSecurityOptionsSql, new { appId = started.NewAppId }, commandTimeout: COMMAND_TIMEOUT_SECONDS));
            wrappedDek = ParseSecurityOptions(security).WrappedDek;
        }

        // Records with a File value, for the progress bar.
        var counts = new long[columns.Count];
        for (var i = 0; i < columns.Count; i++)
            counts[i] = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT_BIG(*) FROM data.t_{columns[i].TableId} WHERE {columns[i].ColumnName} IS NOT NULL",
                commandTimeout: NO_COMMAND_TIMEOUT));
        var total = counts.Sum();
        long done = 0;
        await ReportFilesProgressAsync(connection, started.RunId, done, total);

        var missing = new List<AppCopyWarning>();
        var missingCount = 0;
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            // An encrypted app without a data key has no value anyone can read.
            if (counts[i] == 0 || (column.IsEncrypted && string.IsNullOrEmpty(wrappedDek)))
            {
                done += counts[i];
                continue;
            }

            var selectSql = $"""
                SELECT TOP ({FILE_VALUES_PAGE_SIZE}) Id, {column.ColumnName} AS Value
                FROM data.t_{column.TableId}
                WHERE Id > @afterId AND {column.ColumnName} IS NOT NULL
                ORDER BY Id
                """;
            var updateSql = $"""
                UPDATE t SET {column.ColumnName} = j.[Value]
                FROM data.t_{column.TableId} t
                JOIN OPENJSON(@rows) WITH (Id BIGINT '$.Id', [Value] NVARCHAR(MAX) '$.Value') j ON j.Id = t.Id
                """;

            long afterId = 0;
            while (true)
            {
                var page = (await connection.QueryAsync<FileValueRow>(new CommandDefinition(
                    selectSql, new { afterId }, commandTimeout: NO_COMMAND_TIMEOUT))).ToList();
                if (page.Count == 0) break;

                var values = new List<AppCopyFileValue>(page.Count);
                foreach (var row in page)
                {
                    var plain = column.IsEncrypted
                        ? await TryDecryptAsync(row.Value, wrappedDek!, started.NewAppId)
                        : row.Value;
                    // A value that cannot be read stays as it is: nothing can show or delete its files.
                    if (plain is not null) values.Add(new AppCopyFileValue(row.Id, plain));
                }

                var copied = await copier.CopyPageAsync(values, CancellationToken.None);
                var rows = new List<object>(copied.Values.Count);
                foreach (var value in copied.Values)
                {
                    var stored = column.IsEncrypted && value.Value is not null
                        ? await _encryptionService.EncryptDataAsync(value.Value, wrappedDek!, QueryContext.TenantId, started.NewAppId)
                        : value.Value;
                    rows.Add(new { Id = value.RecordId, Value = stored });
                }
                if (rows.Count > 0)
                    await connection.ExecuteAsync(new CommandDefinition(
                        updateSql, new { rows = JsonSerializer.Serialize(rows) }, commandTimeout: NO_COMMAND_TIMEOUT));

                foreach (var file in copied.Missing)
                {
                    if (++missingCount > MAX_FILE_WARNINGS) continue;
                    missing.Add(new AppCopyWarning
                    {
                        Code = FILE_NOT_FOUND_WARNING,
                        Message = string.Create(CultureInfo.InvariantCulture,
                            $"{column.TableName}, Record ID# {file.RecordId}, {column.FieldName}: the file \"{file.FileName}\" was not found, so it was not copied.")
                    });
                }

                done += page.Count;
                await ReportFilesProgressAsync(connection, started.RunId, done, total);
                afterId = page[^1].Id;
            }
        }

        if (missingCount > MAX_FILE_WARNINGS)
            missing.Add(new AppCopyWarning
            {
                Code = FILE_NOT_FOUND_WARNING,
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"{missingCount - MAX_FILE_WARNINGS:N0} more files were not found, so they were not copied.")
            });
        return missing;
    }

    private async Task<string?> TryDecryptAsync(string? ciphertext, string wrappedDek, long appId)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try
        {
            return await _encryptionService.DecryptDataAsync(ciphertext, wrappedDek, QueryContext.TenantId, appId);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static Task ReportFilesProgressAsync(SqlConnection connection, long runId, long done, long total)
    {
        var percent = DATA_COPY_FILES_PERCENT + (int)((DATA_COPY_END_PERCENT - DATA_COPY_FILES_PERCENT) * done / Math.Max(total, 1));
        var stage = string.Create(CultureInfo.InvariantCulture, $"Copying file attachments: {done:N0} of {total:N0} records");
        return connection.ExecuteAsync(new CommandDefinition(
            FileProgressSql, new { runId, percent, stage }, commandTimeout: COMMAND_TIMEOUT_SECONDS));
    }

    /// <summary>Removes the files a copy that failed had copied so far (their names start with the copy's prefix). Best
    /// effort: a file left behind belongs to a removed app, so nothing shows it.</summary>
    private async Task DeleteCopiedFilesQuietlyAsync(Guid newAppPublicId)
    {
        if (_fileStorage is not IFileStorageCopyService storage) return;
        try
        {
            await storage.DeleteByPrefixAsync(AppCopyFileCopier.FilePrefix(newAppPublicId), CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort, see above.
        }
    }

    /// <summary>Moves the run's LastProgressOn on every few seconds until <paramref name="stop"/>, on a connection of its
    /// own: a stage can go long without progress (a big chunk, the encrypted file paths), and only a run whose process
    /// is gone may look stale. Never throws: the copy's outcome must not depend on its heartbeat.</summary>
    private async Task KeepRunAliveAsync(long runId, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HEARTBEAT_SECONDS));
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stop)) return;
                await using var connection = await OpenConnectionAsync(stop);
                await connection.ExecuteAsync(new CommandDefinition(
                    HeartbeatSql, new { runId }, commandTimeout: HEARTBEAT_SECONDS, cancellationToken: stop));
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;   // the copy ended
            }
            catch (Exception)
            {
                // A missed beat (the database busy for a moment): the next one tries again, and the run looks stale only
                // after several.
            }
        }
    }

    /// <summary>Removes a copy that failed. If that fails too, the run stops moving and the next copy's stale clean-up
    /// removes it.</summary>
    private async Task DiscardQuietlyAsync(long runId, string error, long userId)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(CancellationToken.None);
            await connection.ExecuteAsync(new CommandDefinition(
                DiscardAppCopyProcedure,
                new { RunId = runId, Error = error.Length > MAX_ERROR_LENGTH ? error[..MAX_ERROR_LENGTH] : error, UserId = userId },
                commandTimeout: COMMAND_TIMEOUT_SECONDS, commandType: CommandType.StoredProcedure));
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Left to usp_DiscardStaleAppCopies.
        }
    }

    private static AppSecurityOptionsSettings ParseSecurityOptions(string? securityOptions)
        => string.IsNullOrEmpty(securityOptions) || !securityOptions.TrimStart().StartsWith('{')
            ? new AppSecurityOptionsSettings()
            : JsonSerializer.Deserialize<AppSecurityOptionsSettings>(securityOptions, JsonOptions) ?? new AppSecurityOptionsSettings();

    private static Task SetSecurityOptionsAsync(SqlConnection connection, SqlTransaction transaction, long appId,
        AppSecurityOptionsSettings security, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(
            SetSecurityOptionsSql, new { appId, securityOptions = JsonSerializer.Serialize(security) }, transaction, cancellationToken: ct));

    private static Exception? TranslateSchemaError(Exception ex, AppSchemaCopyRequest request) => ex switch
    {
        SqlException { Number: SQL_NAME_TAKEN } => new DuplicateException("App", "name", request.NewName),
        SqlException { Number: SQL_APP_NOT_FOUND } => new NotFoundException("App", request.SourceAppId),
        _ => null
    };

    /// <summary>Passes the procedure's progress messages on to the store, scaled to 0–<paramref name="scaleTo"/>; every
    /// other informational message is ignored.</summary>
    private void ReportProgress(Guid progressId, long userId, SqlInfoMessageEventArgs e, int scaleTo)
    {
        foreach (SqlError message in e.Errors)
        {
            if (TryParseProgress(message.Message, out var percent, out var stage))
                _progressStore.Report(progressId, userId, percent * scaleTo / 100, stage);
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

    private sealed record SchemaCopy(
        long NewAppId, Guid NewAppPublicId, string? SecurityOptions, List<AppCopyWarning> Warnings, List<AppCopyStageTiming> Stages);

    private sealed record StartedDataCopy(long RunId, long NewAppId, Guid NewAppPublicId, List<AppCopyWarning> Warnings);

    private sealed class CopiedTable
    {
        public long NewTableId { get; set; }
        public long RowTotal { get; set; }
    }

    private sealed class FileColumn
    {
        public long TableId { get; set; }
        public string TableName { get; set; } = string.Empty;
        public string ColumnName { get; set; } = string.Empty;
        public string FieldName { get; set; } = string.Empty;
        public bool IsEncrypted { get; set; }
    }

    private sealed class FileValueRow
    {
        public long Id { get; set; }
        public string? Value { get; set; }
    }

    private sealed class CopyRunProgress
    {
        public int ProgressPercent { get; set; }
        public string Stage { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public Guid NewAppPublicId { get; set; }
        public long RowsCopied { get; set; }
        public string? WarningsJson { get; set; }
        public string? Error { get; set; }
    }
}
