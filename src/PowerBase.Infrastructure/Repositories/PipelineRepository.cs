using Dapper;
using System.Data;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class PipelineRepository : TenantRepositoryBase, IPipelineRepository
{
    private const string PipelineColumns = "Id, PublicId, AppId, Name, Description, VariablesJson, IsActive, IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion";
    
    private const string GetByPublicIdSql = $"""
        SELECT {PipelineColumns}
        FROM meta.Pipeline
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string GetIdByPublicIdSql = """
        SELECT Id FROM meta.Pipeline
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string ListByUserPagedSqlTemplate = $$"""
        SELECT p.Id, p.PublicId, p.AppId, p.Name, p.Description, p.VariablesJson, p.IsActive, p.IsDeleted, p.CreatedOn, p.CreatedBy, p.ModifiedOn, p.ModifiedBy, p.DeletedOn, p.DeletedBy, p.RowVersion,
               s.Type AS FirstStepType,
               s.Subtype AS FirstStepSubtype
        FROM meta.Pipeline p
        LEFT JOIN (
            SELECT PipelineId, Type, Subtype,
                   ROW_NUMBER() OVER (PARTITION BY PipelineId ORDER BY DisplayOrder ASC) as RowNum
            FROM meta.PipelineStep
            WHERE IsDeleted = 0 AND ParentStepId IS NULL
        ) s ON p.Id = s.PipelineId AND s.RowNum = 1
        WHERE p.CreatedBy = @userId AND p.IsDeleted = 0
          AND (@search IS NULL OR p.Name LIKE @search OR p.Description LIKE @search)
          AND (@isActive IS NULL OR p.IsActive = @isActive)
        ORDER BY {0}
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
        """;

    private const string CountByUserSql = """
        SELECT COUNT(1)
        FROM meta.Pipeline p
        WHERE p.CreatedBy = @userId AND p.IsDeleted = 0
          AND (@search IS NULL OR p.Name LIKE @search OR p.Description LIKE @search)
          AND (@isActive IS NULL OR p.IsActive = @isActive)
        """;

    private const string ListAllActiveSql = $"""
        SELECT {PipelineColumns}
        FROM meta.Pipeline
        WHERE IsActive = 1 AND IsDeleted = 0
        """;

    private const string InsertPipelineSql = """
        INSERT INTO meta.Pipeline (AppId, Name, Description, VariablesJson, IsActive, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.PublicId, INSERTED.Id
        VALUES (@appId, @name, @description, @variablesJson, @isActive, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdatePipelineSql = """
        UPDATE meta.Pipeline
        SET Name = @name,
            Description = @description,
            VariablesJson = @variablesJson,
            IsActive = @isActive,
            ModifiedOn = SYSUTCDATETIME(),
            ModifiedBy = @modifiedBy
        WHERE Id = @id AND IsDeleted = 0 AND RowVersion = @rowVersion
        """;

    private const string SoftDeletePipelineSql = """
        UPDATE meta.Pipeline
        SET IsDeleted = 1,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy,
            DeletedOn = SYSUTCDATETIME(), DeletedBy = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string StepColumns = "Id, PublicId, PipelineId, ParentStepId, ParentBranch, RefId, Label, Notes, IsValidated, LastTriggeredOn, DisplayOrder, Type, Subtype, ConfigJson, IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion";

    private const string GetStepsByPipelineIdSql = $"""
        SELECT {StepColumns}
        FROM meta.PipelineStep
        WHERE PipelineId = @pipelineId AND IsDeleted = 0
        ORDER BY ParentStepId ASC, DisplayOrder ASC
        """;

    private const string InsertStepSql = """
        INSERT INTO meta.PipelineStep (PublicId, PipelineId, ParentStepId, ParentBranch, RefId, Label, Notes, IsValidated, LastTriggeredOn, DisplayOrder, Type, Subtype, ConfigJson, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.PublicId, INSERTED.Id
        VALUES (@publicId, @pipelineId, @parentStepId, @parentBranch, @refId, @label, @notes, @isValidated, @lastTriggeredOn, @displayOrder, @type, @subtype, @configJson, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdateStepSql = """
        UPDATE meta.PipelineStep
        SET ParentStepId = @parentStepId,
            ParentBranch = @parentBranch,
            RefId = @refId,
            Label = @label,
            Notes = @notes,
            IsValidated = @isValidated,
            LastTriggeredOn = @lastTriggeredOn,
            DisplayOrder = @displayOrder,
            Type = @type,
            Subtype = @subtype,
            ConfigJson = @configJson,
            ModifiedOn = SYSUTCDATETIME(),
            ModifiedBy = @modifiedBy
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string SoftDeleteStepSql = """
        UPDATE meta.PipelineStep
        SET IsDeleted = 1,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy,
            DeletedOn = SYSUTCDATETIME(), DeletedBy = @modifiedBy
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string ConnectionColumns = "Id, PublicId, PipelineId, Name, Type, CredentialsJson, IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion";

    private const string GetConnectionByPublicIdSql = $"""
        SELECT {ConnectionColumns}
        FROM meta.PipelineConnection
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string GetConnectionsByPipelineIdSql = $"""
        SELECT {ConnectionColumns}
        FROM meta.PipelineConnection
        WHERE PipelineId = @pipelineId AND IsDeleted = 0
        """;

    private const string InsertConnectionSql = """
        INSERT INTO meta.PipelineConnection (PipelineId, Name, Type, CredentialsJson, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.PublicId, INSERTED.Id
        VALUES (@pipelineId, @name, @type, @credentialsJson, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdateConnectionSql = """
        UPDATE meta.PipelineConnection
        SET Name = @name,
            Type = @type,
            CredentialsJson = @credentialsJson,
            ModifiedOn = SYSUTCDATETIME(),
            ModifiedBy = @modifiedBy
        WHERE Id = @id AND IsDeleted = 0
        """;

    private const string SoftDeleteConnectionSql = """
        UPDATE meta.PipelineConnection
        SET IsDeleted = 1,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy,
            DeletedOn = SYSUTCDATETIME(), DeletedBy = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string RunColumns = "Id, PublicId, PipelineId, Status, TriggerType, StartedOn, CompletedOn, TriggeredBy, ErrorMessage, MessageId, AttemptCount, HeartbeatOn, LockedBy, LockedUntil, LastError";

    private const string InsertRunSql = """
        INSERT INTO audit.PipelineRun (PipelineId, Status, TriggerType, StartedOn, TriggeredBy, ErrorMessage, MessageId, AttemptCount, HeartbeatOn, LockedBy, LockedUntil)
        OUTPUT INSERTED.PublicId, INSERTED.Id
        VALUES (@pipelineId, @status, @triggerType, SYSUTCDATETIME(), @triggeredBy, @errorMessage, @messageId, @attemptCount, @heartbeatOn, @lockedBy, @lockedUntil)
        """;

    private const string UpdateRunSql = """
        UPDATE audit.PipelineRun
        SET Status = @status,
            CompletedOn = SYSUTCDATETIME(),
            ErrorMessage = @errorMessage,
            LockedBy = @lockedBy,
            LockedUntil = @lockedUntil,
            LastError = @lastError,
            AttemptCount = @attemptCount
        WHERE Id = @id
        """;

    private const string InsertStepRunSql = """
        INSERT INTO audit.PipelineStepRun (PipelineRunId, StepId, Status, StartedOn, InputContext, OutputContext, LogMessage)
        OUTPUT INSERTED.Id
        VALUES (@pipelineRunId, @stepId, @status, SYSUTCDATETIME(), @inputContext, @outputContext, @logMessage)
        """;

    private const string UpdateStepRunSql = """
        UPDATE audit.PipelineStepRun
        SET Status = @status,
            CompletedOn = SYSUTCDATETIME(),
            InputContext = @inputContext,
            OutputContext = @outputContext,
            LogMessage = @logMessage
        WHERE Id = @id
        """;

    private const string ListRunsSql = $"""
        SELECT {RunColumns}
        FROM audit.PipelineRun
        WHERE PipelineId = @pipelineId
        ORDER BY StartedOn DESC, Id DESC
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
        """;

    private const string CountRunsSql = """
        SELECT COUNT(1)
        FROM audit.PipelineRun
        WHERE PipelineId = @pipelineId
        """;

    private const string GetActivePipelineReferencesForFieldSql = """
        SELECT p.Name AS PipelineName, s.Label AS StepLabel
        FROM meta.PipelineStep s
        JOIN meta.Pipeline p ON s.PipelineId = p.Id
        WHERE p.IsActive = 1 AND p.IsDeleted = 0 AND s.IsDeleted = 0
          AND s.ConfigJson LIKE '%fid[_]' + CAST(@fid AS VARCHAR(10)) + '%'
          AND s.ConfigJson NOT LIKE '%fid[_]' + CAST(@fid AS VARCHAR(10)) + '[0-9]%'
        """;

    private const string GetActivePipelinesReferencingFieldSql = $"""
        SELECT {PipelineColumns}
        FROM meta.PipelineStep s
        JOIN meta.Pipeline p ON s.PipelineId = p.Id
        WHERE p.IsActive = 1 AND p.IsDeleted = 0 AND s.IsDeleted = 0
          AND s.ConfigJson LIKE '%fid[_]' + CAST(@fid AS VARCHAR(10)) + '%'
          AND s.ConfigJson NOT LIKE '%fid[_]' + CAST(@fid AS VARCHAR(10)) + '[0-9]%'
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.Pipeline
            WHERE CreatedBy = @userId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private readonly IControlConnectionFactory _controlConnectionFactory;

    public PipelineRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IControlConnectionFactory controlConnectionFactory)
        : base(connectionFactory, queryContext)
    {
        _controlConnectionFactory = controlConnectionFactory;
    }

    public async Task<Pipeline> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var pipeline = await connection.QuerySingleOrDefaultAsync<Pipeline>(
            new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
        return pipeline ?? throw new NotFoundException("PowerFlow", publicId);
    }

    public async Task<Pipeline?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = "SELECT * FROM meta.Pipeline WHERE Id = @id AND IsDeleted = 0";
        return await connection.QuerySingleOrDefaultAsync<Pipeline>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<long> GetIdByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var id = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetIdByPublicIdSql, new { publicId }, cancellationToken: ct));
        return id ?? throw new NotFoundException("PowerFlow", publicId);
    }

    public async Task<IReadOnlyList<PipelineListItemDetail>> ListByUserPagedAsync(
        long userId,
        int page,
        int pageSize,
        string? search,
        string sortBy,
        bool sortDesc,
        bool? isActive,
        CancellationToken ct = default)
    {
        var column = sortBy switch
        {
            "description" => "p.Description",
            "isActive"    => "p.IsActive",
            "createdOn"   => "p.CreatedOn",
            _             => "p.Name",
        };

        var sql = string.Format(
            ListByUserPagedSqlTemplate,
            $"{column} {(sortDesc ? "DESC" : "ASC")}, p.Id");

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<PipelineListItemDetail>(
            new CommandDefinition(
                sql,
                new
                {
                    userId,
                    search = string.IsNullOrWhiteSpace(search)
                        ? null
                        : $"%{search.Trim()}%",
                    isActive,
                    offset = (page - 1) * pageSize,
                    pageSize
                },
                cancellationToken: ct));
        return results.AsList();
    }

    public async Task<int> CountByUserAsync(
        long userId,
        string? search,
        bool? isActive,
        CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                CountByUserSql,
                new
                {
                    userId,
                    search = string.IsNullOrWhiteSpace(search)
                        ? null
                        : $"%{search.Trim()}%",
                    isActive
                },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Pipeline>> ListAllActiveAsync(CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Pipeline>(
            new CommandDefinition(ListAllActiveSql, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<(Guid PublicId, long Id)> CreateAsync(Pipeline pipeline, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new
        {
            appId = pipeline.AppId,
            name = pipeline.Name,
            description = pipeline.Description,
            variablesJson = pipeline.VariablesJson,
            isActive = pipeline.IsActive,
            createdBy = QueryContext.UserId
        };

        if (transaction is not null)
        {
            return await transaction.Connection!.QuerySingleAsync<(Guid PublicId, long Id)>(
                new CommandDefinition(InsertPipelineSql, parameters, transaction, cancellationToken: ct));
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<(Guid PublicId, long Id)>(
            new CommandDefinition(InsertPipelineSql, parameters, cancellationToken: ct));
    }

    public async Task<int> UpdateAsync(Pipeline pipeline, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new
        {
            id = pipeline.Id,
            name = pipeline.Name,
            description = pipeline.Description,
            variablesJson = pipeline.VariablesJson,
            isActive = pipeline.IsActive,
            modifiedBy = QueryContext.UserId,
            rowVersion = pipeline.RowVersion
        };

        if (transaction is not null)
        {
            var affected1 = await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(UpdatePipelineSql, parameters, transaction, cancellationToken: ct));
            if (affected1 > 0)
            {
                await SyncTriggerSubscriptionsAsync(pipeline.Id, transaction, ct);
            }
            return affected1;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(UpdatePipelineSql, parameters, cancellationToken: ct));
        if (affected > 0)
        {
            await SyncTriggerSubscriptionsAsync(pipeline.Id, null, ct);
        }
        return affected;
    }

    public async Task DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var pipelineId = await GetIdByPublicIdAsync(publicId, ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SoftDeletePipelineSql, new { publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("PowerFlow", publicId);
        await SyncTriggerSubscriptionsAsync(pipelineId, null, ct);
    }

    public async Task SoftDeleteManyAsync(IEnumerable<Guid> publicIds, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.Pipeline
            SET IsDeleted = 1,
                ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy,
                DeletedOn = SYSUTCDATETIME(), DeletedBy = @modifiedBy
            WHERE PublicId IN @publicIds AND IsDeleted = 0
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var pipelineIds = new List<long>();
        foreach (var publicId in publicIds)
        {
            try
            {
                var id = await GetIdByPublicIdAsync(publicId, ct);
                pipelineIds.Add(id);
            }
            catch {}
        }

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql, new { publicIds, modifiedBy = QueryContext.UserId }, cancellationToken: ct));

        if (affected < publicIds.Count())
        {
            throw new ConcurrencyException("One or more PowerFlows were modified or deleted by another process.");
        }

        foreach (var id in pipelineIds)
        {
            await SyncTriggerSubscriptionsAsync(id, null, ct);
        }
    }

    public async Task<IReadOnlyList<PipelineStep>> GetStepsByPipelineIdAsync(long pipelineId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<PipelineStep>(
            new CommandDefinition(GetStepsByPipelineIdSql, new { pipelineId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task SaveStepsAsync(long pipelineId, IEnumerable<PipelineStep> steps, byte[] rowVersion, bool deactivate = false, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        // Internal helper to perform step saves
        async Task DoSaveAsync(IDbConnection connection, IDbTransaction? trans)
        {
            // 0. Concurrency check on meta.Pipeline using rowVersion
            var updatePipelineSql = deactivate ? """
                UPDATE meta.Pipeline
                SET IsActive = 0, ModifiedOn = SYSUTCDATETIME()
                WHERE Id = @pipelineId AND RowVersion = @rowVersion
                """ : """
                UPDATE meta.Pipeline
                SET ModifiedOn = SYSUTCDATETIME()
                WHERE Id = @pipelineId AND RowVersion = @rowVersion
                """;

            var affected = await connection.ExecuteAsync(new CommandDefinition(updatePipelineSql, new { pipelineId, rowVersion }, trans, cancellationToken: ct));
            if (affected == 0)
            {
                throw new ConcurrencyException("Pipeline steps have been modified by another process. Please reload and try again.");
            }

            // 1. Get existing active steps (always unique PublicId per active step!)
            var dbSteps = (await connection.QueryAsync<PipelineStep>(
                new CommandDefinition(GetStepsByPipelineIdSql, new { pipelineId }, trans, cancellationToken: ct)))
                .ToList();

            // Detect duplicate active PublicIds in DB
            var dbPublicIdGroups = dbSteps.GroupBy(s => s.PublicId).Where(g => g.Count() > 1).ToList();
            if (dbPublicIdGroups.Any())
            {
                var duplicateIds = string.Join(", ", dbPublicIdGroups.Select(g => g.Key.ToString()));
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    { "Database", new[] { $"Database integrity error: Duplicate active Step PublicId(s) '{duplicateIds}' found in database." } }
                });
            }

            var existingSteps = dbSteps.ToDictionary(s => s.PublicId);

            // 2. Process incoming steps and check for duplicate PublicIds
            var incomingStepsList = steps.ToList();
            var incomingPublicIds = new HashSet<Guid>();
            foreach (var step in incomingStepsList)
            {
                if (incomingPublicIds.Contains(step.PublicId))
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        { "Steps", new[] { $"Duplicate Step PublicId '{step.PublicId}' found in payload." } }
                    });
                }
                incomingPublicIds.Add(step.PublicId);
            }

            // 3. Soft-delete active steps in the DB that are no longer present in the incoming payload
            foreach (var existing in existingSteps.Values)
            {
                if (!incomingPublicIds.Contains(existing.PublicId))
                {
                    await connection.ExecuteAsync(new CommandDefinition(SoftDeleteStepSql, new
                    {
                        id = existing.Id,
                        modifiedBy = QueryContext.UserId
                    }, trans, cancellationToken: ct));
                }
            }

            // 4. Update or Insert remaining steps (without parent references first)
            var guidToIdMap = new Dictionary<Guid, long>();
            foreach (var step in incomingStepsList)
            {
                step.PipelineId = pipelineId;
                if (existingSteps.TryGetValue(step.PublicId, out var existing))
                {
                    step.Id = existing.Id;
                    
                    // Update active step (setting IsDeleted = 0, clearing deletion metadata)
                    var updateSql = """
                        UPDATE meta.PipelineStep
                        SET ParentStepId = @parentStepId,
                            ParentBranch = @parentBranch,
                            RefId = @refId,
                            Label = @label,
                            Notes = @notes,
                            IsValidated = @isValidated,
                            LastTriggeredOn = @lastTriggeredOn,
                            DisplayOrder = @displayOrder,
                            Type = @type,
                            Subtype = @subtype,
                            ConfigJson = @configJson,
                            IsDeleted = 0,
                            ModifiedOn = SYSUTCDATETIME(),
                            ModifiedBy = @modifiedBy,
                            DeletedOn = NULL,
                            DeletedBy = NULL
                        WHERE Id = @id
                        """;

                    await connection.ExecuteAsync(new CommandDefinition(updateSql, new
                    {
                        id = step.Id,
                        parentStepId = (long?)null,
                        parentBranch = step.ParentBranch,
                        refId = step.RefId,
                        label = step.Label,
                        notes = step.Notes,
                        isValidated = step.IsValidated,
                        lastTriggeredOn = step.LastTriggeredOn,
                        displayOrder = step.DisplayOrder,
                        type = step.Type,
                        subtype = step.Subtype,
                        configJson = step.ConfigJson,
                        modifiedBy = QueryContext.UserId
                    }, trans, cancellationToken: ct));
                    guidToIdMap[step.PublicId] = step.Id;
                }
                else
                {
                    // Check if there is a soft-deleted record with this PublicId to reactivate
                    var softDeleted = await connection.QueryFirstOrDefaultAsync<PipelineStep>(new CommandDefinition(
                        $"SELECT TOP 1 {StepColumns} FROM meta.PipelineStep WHERE PipelineId = @pipelineId AND PublicId = @publicId AND IsDeleted = 1 ORDER BY ModifiedOn DESC, CreatedOn DESC",
                        new { pipelineId, publicId = step.PublicId }, trans, cancellationToken: ct));

                    if (softDeleted != null)
                    {
                        step.Id = softDeleted.Id;

                        // Update and reactivate step (setting IsDeleted = 0, clearing deletion metadata)
                        var updateSql = """
                            UPDATE meta.PipelineStep
                            SET ParentStepId = @parentStepId,
                                ParentBranch = @parentBranch,
                                RefId = @refId,
                                Label = @label,
                                Notes = @notes,
                                IsValidated = @isValidated,
                                LastTriggeredOn = @lastTriggeredOn,
                                DisplayOrder = @displayOrder,
                                Type = @type,
                                Subtype = @subtype,
                                ConfigJson = @configJson,
                                IsDeleted = 0,
                                ModifiedOn = SYSUTCDATETIME(),
                                ModifiedBy = @modifiedBy,
                                DeletedOn = NULL,
                                DeletedBy = NULL
                            WHERE Id = @id
                            """;

                        await connection.ExecuteAsync(new CommandDefinition(updateSql, new
                        {
                            id = step.Id,
                            parentStepId = (long?)null,
                            parentBranch = step.ParentBranch,
                            refId = step.RefId,
                            label = step.Label,
                            notes = step.Notes,
                            isValidated = step.IsValidated,
                            lastTriggeredOn = step.LastTriggeredOn,
                            displayOrder = step.DisplayOrder,
                            type = step.Type,
                            subtype = step.Subtype,
                            configJson = step.ConfigJson,
                            modifiedBy = QueryContext.UserId
                        }, trans, cancellationToken: ct));
                        guidToIdMap[step.PublicId] = step.Id;
                    }
                    else
                    {
                        // Insert new step (set parent pointer to null temporarily)
                        var inserted = await connection.QuerySingleAsync<(Guid PublicId, long Id)>(new CommandDefinition(InsertStepSql, new
                        {
                            publicId = step.PublicId,
                            pipelineId = step.PipelineId,
                            parentStepId = (long?)null,
                            parentBranch = step.ParentBranch,
                            refId = step.RefId,
                            label = step.Label,
                            notes = step.Notes,
                            isValidated = step.IsValidated,
                            lastTriggeredOn = step.LastTriggeredOn,
                            displayOrder = step.DisplayOrder,
                            type = step.Type,
                            subtype = step.Subtype,
                            configJson = step.ConfigJson,
                            createdBy = QueryContext.UserId
                        }, trans, cancellationToken: ct));
                        
                        step.Id = inserted.Id;
                        guidToIdMap[step.PublicId] = step.Id;
                    }
                }
            }

            // 5. Second pass: Link parent relations using ParentPublicId
            foreach (var step in incomingStepsList)
            {
                if (step.ParentPublicId.HasValue && guidToIdMap.TryGetValue(step.ParentPublicId.Value, out var parentId))
                {
                    step.ParentStepId = parentId;
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE meta.PipelineStep SET ParentStepId = @parentStepId, ParentBranch = @parentBranch WHERE Id = @id",
                        new { id = step.Id, parentStepId = parentId, parentBranch = step.ParentBranch },
                        trans,
                        cancellationToken: ct));
                }
            }
        }

        if (transaction is not null)
        {
            await DoSaveAsync(transaction.Connection!, transaction);
            await SyncTriggerSubscriptionsAsync(pipelineId, transaction, ct);
            return;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        await using var localTx = await connection.BeginTransactionAsync(ct);
        try
        {
            await DoSaveAsync(connection, localTx);
            await SyncTriggerSubscriptionsAsync(pipelineId, localTx, ct);
            await localTx.CommitAsync(ct);
        }
        catch
        {
            await localTx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<PipelineConnection?> GetConnectionByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PipelineConnection>(
            new CommandDefinition(GetConnectionByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PipelineConnection>> GetConnectionsByPipelineIdAsync(long pipelineId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<PipelineConnection>(
            new CommandDefinition(GetConnectionsByPipelineIdSql, new { pipelineId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<(Guid PublicId, long Id)> CreateConnectionAsync(PipelineConnection conn, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new
        {
            pipelineId = conn.PipelineId,
            name = conn.Name,
            type = conn.Type,
            credentialsJson = conn.CredentialsJson,
            createdBy = QueryContext.UserId
        };

        if (transaction is not null)
        {
            return await transaction.Connection!.QuerySingleAsync<(Guid PublicId, long Id)>(
                new CommandDefinition(InsertConnectionSql, parameters, transaction, cancellationToken: ct));
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<(Guid PublicId, long Id)>(
            new CommandDefinition(InsertConnectionSql, parameters, cancellationToken: ct));
    }

    public async Task<int> UpdateConnectionAsync(PipelineConnection conn, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var parameters = new
        {
            id = conn.Id,
            name = conn.Name,
            type = conn.Type,
            credentialsJson = conn.CredentialsJson,
            modifiedBy = QueryContext.UserId
        };

        if (transaction is not null)
        {
            return await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(UpdateConnectionSql, parameters, transaction, cancellationToken: ct));
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateConnectionSql, parameters, cancellationToken: ct));
    }

    public async Task DeleteConnectionAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteConnectionSql, new { publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("PipelineConnection", publicId);
    }

    public async Task<(Guid PublicId, long Id)> CreateRunAsync(PipelineRun run, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<(Guid PublicId, long Id)>(
            new CommandDefinition(InsertRunSql, new
            {
                pipelineId = run.PipelineId,
                status = run.Status,
                triggerType = run.TriggerType,
                triggeredBy = run.TriggeredBy != 0 ? run.TriggeredBy : QueryContext.UserId,
                errorMessage = run.ErrorMessage,
                messageId = run.MessageId,
                attemptCount = run.AttemptCount,
                heartbeatOn = run.HeartbeatOn,
                lockedBy = run.LockedBy,
                lockedUntil = run.LockedUntil
            }, cancellationToken: ct));
    }

    public async Task UpdateRunAsync(PipelineRun run, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateRunSql, new
            {
                id = run.Id,
                status = run.Status,
                errorMessage = run.ErrorMessage,
                lockedBy = run.LockedBy,
                lockedUntil = run.LockedUntil,
                lastError = run.LastError,
                attemptCount = run.AttemptCount
            }, cancellationToken: ct));
    }

    public async Task<PipelineRun?> GetRunByMessageIdAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = "SELECT * FROM audit.PipelineRun WHERE MessageId = @messageId";
        return await connection.QuerySingleOrDefaultAsync<PipelineRun>(
            new CommandDefinition(sql, new { messageId }, cancellationToken: ct));
    }

    public async Task<long> CreateRunAttemptAsync(PipelineRunAttempt attempt, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            INSERT INTO audit.PipelineRunAttempt (PipelineRunId, AttemptNumber, Status, StartedOn)
            OUTPUT inserted.Id
            VALUES (@PipelineRunId, @AttemptNumber, @Status, SYSUTCDATETIME())
            """;
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, attempt, cancellationToken: ct));
    }

    public async Task UpdateRunAttemptAsync(PipelineRunAttempt attempt, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            UPDATE audit.PipelineRunAttempt
            SET Status = @Status,
                CompletedOn = SYSUTCDATETIME(),
                LastError = @LastError
            WHERE Id = @Id
            """;
        await connection.ExecuteAsync(
            new CommandDefinition(sql, attempt, cancellationToken: ct));
    }

    public async Task<bool> ReclaimStaleRunAsync(Guid messageId, string workerId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            UPDATE audit.PipelineRun
            SET LockedBy = @workerId,
                LockedUntil = DATEADD(second, 45, SYSUTCDATETIME()),
                HeartbeatOn = SYSUTCDATETIME(),
                AttemptCount = AttemptCount + 1
            OUTPUT inserted.Id
            WHERE MessageId = @messageId
              AND Status = 'Running'
              AND LockedUntil <= SYSUTCDATETIME()
              AND AttemptCount < 5;
            """;
        var id = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(sql, new { messageId, workerId }, cancellationToken: ct));
        return id.HasValue;
    }

    public async Task<bool> ClaimFailedRunRetryAsync(Guid messageId, string workerId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            UPDATE audit.PipelineRun
            SET LockedBy = @workerId,
                LockedUntil = DATEADD(second, 45, SYSUTCDATETIME()),
                HeartbeatOn = SYSUTCDATETIME(),
                Status = 'Running',
                AttemptCount = AttemptCount + 1
            OUTPUT inserted.Id
            WHERE MessageId = @messageId
              AND Status = 'Failed'
              AND AttemptCount < 5;
            """;
        var id = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(sql, new { messageId, workerId }, cancellationToken: ct));
        return id.HasValue;
    }

    public async Task ExtendRunLeaseAsync(Guid messageId, string workerId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            UPDATE audit.PipelineRun
            SET HeartbeatOn = SYSUTCDATETIME(),
                LockedUntil = DATEADD(second, 45, SYSUTCDATETIME())
            WHERE MessageId = @messageId
              AND LockedBy = @workerId
              AND Status = 'Running';
            """;
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { messageId, workerId }, cancellationToken: ct));
    }

    public async Task<long> CreateStepRunAsync(PipelineStepRun stepRun, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(InsertStepRunSql, new
            {
                pipelineRunId = stepRun.PipelineRunId,
                stepId = stepRun.StepId,
                status = stepRun.Status,
                inputContext = stepRun.InputContext,
                outputContext = stepRun.OutputContext,
                logMessage = stepRun.LogMessage
            }, cancellationToken: ct));
    }

    public async Task UpdateStepRunAsync(PipelineStepRun stepRun, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateStepRunSql, new
            {
                id = stepRun.Id,
                status = stepRun.Status,
                inputContext = stepRun.InputContext,
                outputContext = stepRun.OutputContext,
                logMessage = stepRun.LogMessage
            }, cancellationToken: ct));
    }

    public Task<IReadOnlyList<PipelineStepRun>> GetStepRunsByRunIdAsync(long runId, CancellationToken ct = default)
    {
        return GetStepRunsByRunIdAsync(runId, 1, 2000, ct);
    }

    public async Task<IReadOnlyList<PipelineStepRun>> GetStepRunsByRunIdAsync(long runId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            SELECT Id, PipelineRunId, StepId, Status, StartedOn, CompletedOn, InputContext, OutputContext, LogMessage
            FROM audit.PipelineStepRun
            WHERE PipelineRunId = @runId
            ORDER BY StartedOn ASC, Id ASC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;
        var offset = (page - 1) * pageSize;
        var results = await connection.QueryAsync<PipelineStepRun>(
            new CommandDefinition(sql, new { runId, offset, pageSize }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<int> CountStepRunsByRunIdAsync(long runId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = "SELECT COUNT(*) FROM audit.PipelineStepRun WHERE PipelineRunId = @runId";
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { runId }, cancellationToken: ct));
    }

    public async Task<PipelineRun?> GetRunByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = "SELECT * FROM audit.PipelineRun WHERE PublicId = @publicId";
        return await connection.QuerySingleOrDefaultAsync<PipelineRun>(
            new CommandDefinition(sql, new { publicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PipelineRun>> GetRunsByPipelineIdAsync(long pipelineId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<PipelineRun>(
            new CommandDefinition(ListRunsSql, new { pipelineId, offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<int> CountRunsByPipelineIdAsync(long pipelineId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountRunsSql, new { pipelineId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<(string PipelineName, string StepLabel)>> GetActivePipelineReferencesForFieldAsync(int fid, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<(string PipelineName, string StepLabel)>(
            new CommandDefinition(GetActivePipelineReferencesForFieldSql, new { fid }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<Pipeline>> GetActivePipelinesReferencingFieldAsync(int fid, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Pipeline>(
            new CommandDefinition(GetActivePipelinesReferencingFieldSql, new { fid }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<string>> GetPipelineNamesForUserAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var sql = "SELECT Name FROM meta.Pipeline WHERE CreatedBy = @userId AND IsDeleted = 0";
        var results = await connection.QueryAsync<string>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<bool> NameExistsForUserAsync(long userId, string name, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { userId, name }, cancellationToken: ct));
    }

    public async Task<byte[]> GetRowVersionAsync(long pipelineId, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var sql = "SELECT RowVersion FROM meta.Pipeline WHERE Id = @pipelineId AND IsDeleted = 0";
        if (transaction is not null)
        {
            return await transaction.Connection!.ExecuteScalarAsync<byte[]>(
                new CommandDefinition(sql, new { pipelineId }, transaction, cancellationToken: ct));
        }
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<byte[]>(
            new CommandDefinition(sql, new { pipelineId }, cancellationToken: ct));
    }

    public async Task InvalidateStepsReferencingFieldAsync(int fid, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var sql = """
            UPDATE meta.PipelineStep
            SET IsValidated = 0,
                ModifiedOn = SYSUTCDATETIME()
            WHERE IsDeleted = 0
              AND ConfigJson LIKE '%fid[_]' + CAST(@fid AS VARCHAR(10)) + '%'
              AND ConfigJson NOT LIKE '%fid[_]' + CAST(@fid AS VARCHAR(10)) + '[0-9]%'
            """;
        var cmd = new CommandDefinition(sql, new { fid }, transaction, cancellationToken: ct);
        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(cmd);
            return;
        }
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(cmd);
    }

    public async Task<PipelineStep?> GetStepByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            SELECT s.*
            FROM meta.PipelineStep s
            INNER JOIN meta.Pipeline p ON s.PipelineId = p.Id
            WHERE s.PublicId = @publicId AND s.IsDeleted = 0 AND p.IsDeleted = 0
            """;
        return await connection.QuerySingleOrDefaultAsync<PipelineStep>(
            new CommandDefinition(sql, new { publicId }, cancellationToken: ct));
    }

    public async Task<bool> UpdateStepLastTriggeredOnAsync(long stepId, DateTime? oldTime, DateTime newTime, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            UPDATE meta.PipelineStep
            SET LastTriggeredOn = @newTime, ModifiedOn = SYSUTCDATETIME()
            WHERE Id = @stepId AND (LastTriggeredOn = @oldTime OR (LastTriggeredOn IS NULL AND @oldTime IS NULL))
            """;
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { stepId, oldTime, newTime }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<IReadOnlyList<PipelineStep>> GetActiveScheduleStepsAsync(CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            SELECT s.*
            FROM meta.PipelineStep s
            INNER JOIN meta.Pipeline p ON s.PipelineId = p.Id
            WHERE s.Subtype = 'schedule' AND s.IsDeleted = 0 AND p.IsDeleted = 0 AND p.IsActive = 1
            """;
        var steps = await connection.QueryAsync<PipelineStep>(
            new CommandDefinition(sql, cancellationToken: ct));
        return steps.ToList();
    }

    public async Task<PipelineSchedule?> GetScheduleByPipelineIdAsync(long pipelineId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = "SELECT * FROM meta.PipelineSchedule WHERE PipelineId = @pipelineId AND IsDeleted = 0";
        return await connection.QuerySingleOrDefaultAsync<PipelineSchedule>(
            new CommandDefinition(sql, new { pipelineId }, cancellationToken: ct));
    }

    public async Task<PipelineSchedule?> GetScheduleByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = "SELECT * FROM meta.PipelineSchedule WHERE PublicId = @publicId AND IsDeleted = 0";
        return await connection.QuerySingleOrDefaultAsync<PipelineSchedule>(
            new CommandDefinition(sql, new { publicId }, cancellationToken: ct));
    }

    public async Task<(Guid PublicId, long Id)> CreateScheduleAsync(PipelineSchedule schedule, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO meta.PipelineSchedule (PipelineId, ScheduleType, Interval, TimeOfDay, Weekdays, MonthDay, MonthOfYear, RelativeWeek, RelativeDay, TimeZone, CronExpression, NextRunOn, LastRunOn, IsDeleted, CreatedOn, CreatedBy)
            OUTPUT INSERTED.PublicId, INSERTED.Id
            VALUES (@pipelineId, @scheduleType, @interval, @timeOfDay, @weekdays, @monthDay, @monthOfYear, @relativeWeek, @relativeDay, @timeZone, @cronExpression, @nextRunOn, @lastRunOn, 0, SYSUTCDATETIME(), @createdBy)
            """;

        var parameters = new
        {
            pipelineId = schedule.PipelineId,
            scheduleType = schedule.ScheduleType,
            interval = schedule.Interval,
            timeOfDay = schedule.TimeOfDay,
            weekdays = schedule.Weekdays,
            monthDay = schedule.MonthDay,
            monthOfYear = schedule.MonthOfYear,
            relativeWeek = schedule.RelativeWeek,
            relativeDay = schedule.RelativeDay,
            timeZone = schedule.TimeZone,
            cronExpression = schedule.CronExpression,
            nextRunOn = schedule.NextRunOn,
            lastRunOn = schedule.LastRunOn,
            createdBy = QueryContext.UserId
        };

        if (transaction is not null)
        {
            return await transaction.Connection!.QuerySingleAsync<(Guid PublicId, long Id)>(
                new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<(Guid PublicId, long Id)>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task<int> UpdateScheduleAsync(PipelineSchedule schedule, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineSchedule
            SET ScheduleType = @scheduleType,
                Interval = @interval,
                TimeOfDay = @timeOfDay,
                Weekdays = @weekdays,
                MonthDay = @monthDay,
                MonthOfYear = @monthOfYear,
                RelativeWeek = @relativeWeek,
                RelativeDay = @relativeDay,
                TimeZone = @timeZone,
                CronExpression = @cronExpression,
                NextRunOn = @nextRunOn,
                LastRunOn = @lastRunOn,
                ModifiedOn = SYSUTCDATETIME(),
                ModifiedBy = @modifiedBy
            WHERE Id = @id AND IsDeleted = 0
            """;

        var parameters = new
        {
            id = schedule.Id,
            scheduleType = schedule.ScheduleType,
            interval = schedule.Interval,
            timeOfDay = schedule.TimeOfDay,
            weekdays = schedule.Weekdays,
            monthDay = schedule.MonthDay,
            monthOfYear = schedule.MonthOfYear,
            relativeWeek = schedule.RelativeWeek,
            relativeDay = schedule.RelativeDay,
            timeZone = schedule.TimeZone,
            cronExpression = schedule.CronExpression,
            nextRunOn = schedule.NextRunOn,
            lastRunOn = schedule.LastRunOn,
            modifiedBy = QueryContext.UserId
        };

        if (transaction is not null)
        {
            return await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task DeleteScheduleAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            UPDATE meta.PipelineSchedule
            SET IsDeleted = 1,
                ModifiedOn = SYSUTCDATETIME(),
                ModifiedBy = @modifiedBy
            WHERE PublicId = @publicId AND IsDeleted = 0
            """;
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("PipelineSchedule", publicId);
    }

    public async Task<IReadOnlyList<PipelineSchedule>> GetActivePipelineSchedulesAsync(CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            SELECT s.*
            FROM meta.PipelineSchedule s
            INNER JOIN meta.Pipeline p ON s.PipelineId = p.Id
            WHERE s.IsDeleted = 0 AND p.IsActive = 1 AND p.IsDeleted = 0
            """;
        var schedules = await connection.QueryAsync<PipelineSchedule>(
            new CommandDefinition(sql, cancellationToken: ct));
        return schedules.ToList();
    }

    public async Task<bool> UpdateScheduleLastAndNextRunOnAsync(long scheduleId, DateTime? oldLastRun, DateTime newLastRun, DateTime? newNextRun, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            UPDATE meta.PipelineSchedule
            SET LastRunOn = @newLastRun,
                NextRunOn = @newNextRun,
                ModifiedOn = SYSUTCDATETIME()
            WHERE Id = @scheduleId AND (LastRunOn = @oldLastRun OR (LastRunOn IS NULL AND @oldLastRun IS NULL))
            """;
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { scheduleId, oldLastRun, newLastRun, newNextRun }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<long> CreateOutboxItemAsync(PipelineOutboxItem item, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO meta.PipelineOutbox (
                PipelineId, TriggerEvent, TriggerPayloadJson, TriggeredBy, TriggerTablePublicId,
                CorrelationId, Depth, PipelineChain, MessageId, BatchId, PayloadVersion, CreatedOn, Published
            )
            OUTPUT inserted.Id
            VALUES (
                @PipelineId, @TriggerEvent, @TriggerPayloadJson, @TriggeredBy, @TriggerTablePublicId,
                @CorrelationId, @Depth, @PipelineChain, @MessageId, @BatchId, @PayloadVersion, SYSUTCDATETIME(), 0
            )
            """;

        if (transaction is not null)
        {
            return await transaction.Connection!.ExecuteScalarAsync<long>(
                new CommandDefinition(sql, item, transaction, cancellationToken: ct));
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, item, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PipelineOutboxItem>> ClaimOutboxItemsAsync(string workerId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineOutbox
            SET LockedBy = @workerId, LockedUntil = DATEADD(minute, 2, SYSUTCDATETIME())
            OUTPUT inserted.*
            WHERE Id IN (
                SELECT TOP 50 Id 
                FROM meta.PipelineOutbox WITH (UPDLOCK, READPAST)
                WHERE Published = 0 
                  AND (LockedUntil IS NULL OR LockedUntil <= SYSUTCDATETIME()) 
                  AND (NextAttemptOn IS NULL OR NextAttemptOn <= SYSUTCDATETIME())
                  AND AttemptCount < 5
                ORDER BY Id ASC
            );
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var items = await connection.QueryAsync<PipelineOutboxItem>(
            new CommandDefinition(sql, new { workerId }, cancellationToken: ct));
        return items.ToList();
    }

    public async Task UpdateOutboxItemStatusAsync(long id, string workerId, byte status, DateTime? publishedOn = null, DateTime? failedOn = null, string? error = null, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE meta.PipelineOutbox
            SET Published = @status,
                PublishedOn = @publishedOn,
                FailedOn = @failedOn,
                LastError = @error,
                LockedBy = NULL,
                LockedUntil = NULL,
                AttemptCount = CASE WHEN @status = 1 THEN AttemptCount ELSE AttemptCount + 1 END,
                NextAttemptOn = CASE WHEN @status = 1 THEN NULL ELSE DATEADD(second, POWER(2, AttemptCount + 2), SYSUTCDATETIME()) END
            WHERE Id = @id AND LockedBy = @workerId;
            """;

        var parameters = new { id, workerId, status, publishedOn, failedOn, error };

        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
            return;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task PruneOutboxItemsAsync(DateTime olderThan, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM meta.PipelineOutbox WHERE Published = 1 AND PublishedOn <= @olderThan";
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { olderThan }, cancellationToken: ct));
    }

    public async Task SyncTriggerSubscriptionsAsync(long pipelineId, IDbTransaction? tenantTransaction = null, CancellationToken ct = default)
    {
        Pipeline? pipeline;
        if (tenantTransaction != null)
        {
            const string sql = "SELECT * FROM meta.Pipeline WHERE Id = @pipelineId";
            pipeline = await tenantTransaction.Connection!.QuerySingleOrDefaultAsync<Pipeline>(
                new CommandDefinition(sql, new { pipelineId }, tenantTransaction, cancellationToken: ct));
        }
        else
        {
            pipeline = await GetByIdAsync(pipelineId, ct);
        }

        IReadOnlyList<PipelineStep> steps;
        if (tenantTransaction != null)
        {
            steps = (await tenantTransaction.Connection!.QueryAsync<PipelineStep>(
                new CommandDefinition(GetStepsByPipelineIdSql, new { pipelineId }, tenantTransaction, cancellationToken: ct))).ToList();
        }
        else
        {
            steps = await GetStepsByPipelineIdAsync(pipelineId, ct);
        }

        var triggerStep = (pipeline == null || pipeline.IsDeleted) 
            ? null 
            : steps.FirstOrDefault(s => !s.IsDeleted && s.Type == "trigger" && (s.Subtype == "new-event" || s.Subtype == "new-bulk-event"));

        if (pipeline == null || pipeline.IsDeleted || !pipeline.IsActive || triggerStep == null || string.IsNullOrEmpty(triggerStep.ConfigJson))
        {
            var deleteSql = "DELETE FROM meta.PipelineTriggerSubscription WHERE OwnerTenantId = @ownerTenantId AND PipelinePublicId = @pipelinePublicId";
            await using var controlConn = _controlConnectionFactory.Create();
            await controlConn.OpenAsync(ct);
            var pipelinePublicId = pipeline?.PublicId ?? await GetPublicIdFromIdHelperAsync(pipelineId, tenantTransaction, ct);
            if (pipelinePublicId != Guid.Empty)
            {
                await controlConn.ExecuteAsync(new CommandDefinition(deleteSql, new { ownerTenantId = QueryContext.TenantId, pipelinePublicId }, cancellationToken: ct));
            }
            return;
        }

        NewEventStepConfig config;
        try
        {
            config = System.Text.Json.JsonSerializer.Deserialize<NewEventStepConfig>(triggerStep.ConfigJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException();
        }
        catch
        {
            var deleteSql = "DELETE FROM meta.PipelineTriggerSubscription WHERE OwnerTenantId = @ownerTenantId AND PipelinePublicId = @pipelinePublicId";
            await using var controlConn = _controlConnectionFactory.Create();
            await controlConn.OpenAsync(ct);
            await controlConn.ExecuteAsync(new CommandDefinition(deleteSql, new { ownerTenantId = QueryContext.TenantId, pipelinePublicId = pipeline.PublicId }, cancellationToken: ct));
            return;
        }

        long targetTenantId = QueryContext.TenantId;
        if (Guid.TryParse(config.ConnectionPublicId, out var connectionGuid) && !PowerBase.Application.Pipelines.PipelineStepValidator.SystemConnectionIds.Contains(connectionGuid))
        {
            PipelineAccount? account;
            if (tenantTransaction != null)
            {
                const string sql = "SELECT * FROM meta.PipelineAccount WHERE PublicId = @connectionGuid AND IsDeleted = 0";
                account = await tenantTransaction.Connection!.QuerySingleOrDefaultAsync<PipelineAccount>(
                    new CommandDefinition(sql, new { connectionGuid }, tenantTransaction, cancellationToken: ct));
            }
            else
            {
                const string sql = "SELECT * FROM meta.PipelineAccount WHERE PublicId = @connectionGuid AND IsDeleted = 0";
                await using var tenantConn = await ConnectionFactory.CreateAsync(ct);
                account = await tenantConn.QuerySingleOrDefaultAsync<PipelineAccount>(
                    new CommandDefinition(sql, new { connectionGuid }, cancellationToken: ct));
            }

            if (account != null)
            {
                targetTenantId = account.TargetTenantId;
            }
            else
            {
                const string sql = "SELECT Id FROM meta.Tenant WHERE PublicId = @connectionGuid AND IsDeleted = 0";
                await using var controlConnForTenant = _controlConnectionFactory.Create();
                await controlConnForTenant.OpenAsync(ct);
                var resolvedId = await controlConnForTenant.QuerySingleOrDefaultAsync<long?>(
                    new CommandDefinition(sql, new { connectionGuid }, cancellationToken: ct));
                if (resolvedId.HasValue)
                {
                    targetTenantId = resolvedId.Value;
                }
            }
        }

        if (string.IsNullOrEmpty(config.AppPublicId) || !Guid.TryParse(config.AppPublicId, out var targetAppPublicId) ||
            string.IsNullOrEmpty(config.TablePublicId) || !Guid.TryParse(config.TablePublicId, out var targetTablePublicId))
        {
            return;
        }

        var upsertSql = """
            MERGE meta.PipelineTriggerSubscription AS target
            USING (SELECT @OwnerTenantId AS OwnerTenantId, @PipelinePublicId AS PipelinePublicId, @TriggerStepRefId AS TriggerStepRefId) AS source
            ON target.OwnerTenantId = source.OwnerTenantId 
               AND target.PipelinePublicId = source.PipelinePublicId 
               AND target.TriggerStepRefId = source.TriggerStepRefId
            WHEN MATCHED THEN
                UPDATE SET 
                    OwnerPipelineId = @OwnerPipelineId,
                    TriggerStepPublicId = @TriggerStepPublicId,
                    TargetTenantId = @TargetTenantId,
                    TargetAppPublicId = @TargetAppPublicId,
                    TargetTablePublicId = @TargetTablePublicId,
                    TargetConnectionPublicId = @TargetConnectionPublicId,
                    TriggerOnAdded = @TriggerOnAdded,
                    TriggerOnModified = @TriggerOnModified,
                    TriggerOnDeleted = @TriggerOnDeleted,
                    TriggerOnAnyField = @TriggerOnAnyField,
                    TriggerFieldsJson = @TriggerFieldsJson,
                    FiltersJson = @FiltersJson,
                    FilterGroupsJson = @FilterGroupsJson,
                    LimitRecords = @LimitRecords,
                    MaxRecords = @MaxRecords,
                    TriggerSubtype = @TriggerSubtype,
                    IsActive = @IsActive,
                    LastModifiedOn = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (OwnerTenantId, OwnerPipelineId, PipelinePublicId, TriggerStepPublicId, TriggerStepRefId, TargetTenantId, TargetAppPublicId, TargetTablePublicId, TargetConnectionPublicId, TriggerOnAdded, TriggerOnModified, TriggerOnDeleted, TriggerOnAnyField, TriggerFieldsJson, FiltersJson, FilterGroupsJson, LimitRecords, MaxRecords, TriggerSubtype, IsActive, CreatedOn, LastModifiedOn)
                VALUES (@OwnerTenantId, @OwnerPipelineId, @PipelinePublicId, @TriggerStepPublicId, @TriggerStepRefId, @TargetTenantId, @TargetAppPublicId, @TargetTablePublicId, @TargetConnectionPublicId, @TriggerOnAdded, @TriggerOnModified, @TriggerOnDeleted, @TriggerOnAnyField, @TriggerFieldsJson, @FiltersJson, @FilterGroupsJson, @LimitRecords, @MaxRecords, @TriggerSubtype, @IsActive, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        var parameters = new
        {
            OwnerTenantId = QueryContext.TenantId,
            OwnerPipelineId = pipelineId,
            PipelinePublicId = pipeline.PublicId,
            TriggerStepPublicId = triggerStep.PublicId,
            TriggerStepRefId = triggerStep.RefId,
            TargetTenantId = targetTenantId,
            TargetAppPublicId = targetAppPublicId,
            TargetTablePublicId = targetTablePublicId,
            TargetConnectionPublicId = connectionGuid,
            TriggerOnAdded = config.TriggerOnAdded,
            TriggerOnModified = config.TriggerOnModified,
            TriggerOnDeleted = config.TriggerOnDeleted,
            TriggerOnAnyField = config.TriggerOnAnyField,
            TriggerFieldsJson = config.TriggerFields != null ? System.Text.Json.JsonSerializer.Serialize(config.TriggerFields) : null,
            FiltersJson = config.Filters != null ? System.Text.Json.JsonSerializer.Serialize(config.Filters) : null,
            FilterGroupsJson = config.FilterGroups != null ? System.Text.Json.JsonSerializer.Serialize(config.FilterGroups) : null,
            LimitRecords = config.LimitRecords,
            MaxRecords = config.MaxRecords,
            TriggerSubtype = triggerStep.Subtype,
            IsActive = pipeline.IsActive
        };

        await using var controlConn2 = _controlConnectionFactory.Create();
        await controlConn2.OpenAsync(ct);
        await controlConn2.ExecuteAsync(new CommandDefinition(upsertSql, parameters, cancellationToken: ct));
    }

    private async Task<Guid> GetPublicIdFromIdHelperAsync(long pipelineId, IDbTransaction? transaction, CancellationToken ct)
    {
        const string sql = "SELECT PublicId FROM meta.Pipeline WHERE Id = @pipelineId";
        if (transaction != null)
        {
            return await transaction.Connection!.QuerySingleOrDefaultAsync<Guid>(
                new CommandDefinition(sql, new { pipelineId }, transaction, cancellationToken: ct));
        }
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Guid>(new CommandDefinition(sql, new { pipelineId }, cancellationToken: ct));
    }

    private class NewEventStepConfig
    {
        public string? ConnectionPublicId { get; set; }
        public string? AppPublicId { get; set; }
        public string? TablePublicId { get; set; }
        public bool TriggerOnAdded { get; set; }
        public bool TriggerOnModified { get; set; }
        public bool TriggerOnDeleted { get; set; }
        public bool TriggerOnAnyField { get; set; }
        public List<string>? TriggerFields { get; set; }
        public List<string>? SubsequentFields { get; set; }
        public bool LimitRecords { get; set; }
        public int? MaxRecords { get; set; }
        public List<PowerBase.Application.Pipelines.TriggerFilterRule>? Filters { get; set; }
        public List<PowerBase.Application.Pipelines.TriggerFilterGroup>? FilterGroups { get; set; }
    }

    public async Task InsertBulkEventRecordsAsync(List<PipelineBulkEventRecord> records, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (records == null || !records.Any()) return;
        const string sql = """
            INSERT INTO meta.PipelineBulkEventRecord (BulkEventId, Ordinal, RecordPublicId, EventType, BeforeValuesJson, AfterValuesJson, ChangedFieldsJson, Processed, CreatedOn)
            VALUES (@BulkEventId, @Ordinal, @RecordPublicId, @EventType, @BeforeValuesJson, @AfterValuesJson, @ChangedFieldsJson, @Processed, @CreatedOn)
            """;

        if (transaction != null)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, records, transaction, cancellationToken: ct));
            return;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, records, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PipelineBulkEventRecord>> GetBulkEventRecordsPreviewAsync(Guid bulkEventId, int limit, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            SELECT TOP (@limit) Id, BulkEventId, Ordinal, RecordPublicId, EventType, BeforeValuesJson, AfterValuesJson, ChangedFieldsJson, Processed, CreatedOn
            FROM meta.PipelineBulkEventRecord
            WHERE BulkEventId = @bulkEventId
            ORDER BY Ordinal ASC
            """;
        var results = await connection.QueryAsync<PipelineBulkEventRecord>(new CommandDefinition(sql, new { bulkEventId, limit }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<PipelineBulkEventRecord>> GetPendingBulkEventRecordsPageAsync(Guid bulkEventId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            SELECT Id, BulkEventId, Ordinal, RecordPublicId, EventType, BeforeValuesJson, AfterValuesJson, ChangedFieldsJson, Processed, CreatedOn
            FROM meta.PipelineBulkEventRecord
            WHERE BulkEventId = @bulkEventId AND Processed = 0
            ORDER BY Ordinal ASC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;
        var offset = (page - 1) * pageSize;
        var results = await connection.QueryAsync<PipelineBulkEventRecord>(new CommandDefinition(sql, new { bulkEventId, offset, pageSize }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task MarkBulkEventRecordsProcessedAsync(List<long> ids, byte processedStatus, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (ids == null || !ids.Any()) return;
        const string sql = "UPDATE meta.PipelineBulkEventRecord SET Processed = @processedStatus WHERE Id IN @ids";

        if (transaction != null)
        {
            await transaction.Connection!.ExecuteAsync(new CommandDefinition(sql, new { ids, processedStatus }, transaction, cancellationToken: ct));
            return;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { ids, processedStatus }, cancellationToken: ct));
    }

    public async Task DeleteExpiredBulkEventRecordsAsync(DateTime createdBefore, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        const string sql = """
            DELETE r
            FROM meta.PipelineBulkEventRecord r
            INNER JOIN audit.PipelineRun run ON run.MessageId = r.BulkEventId
            WHERE run.Status IN ('Success', 'Failed', 'Skipped', 'Stopped')
              AND r.CreatedOn <= @createdBefore
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, new { createdBefore }, cancellationToken: ct));
    }
}

