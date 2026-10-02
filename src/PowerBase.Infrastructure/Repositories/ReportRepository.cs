using Dapper;
using PowerBase.Application.Reports;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class ReportRepository : TenantRepositoryBase, IReportRepository
{
    private const string SelectColumns = """
        r.Id, r.PublicId, r.AppTableId, r.OwnerId, r.Name, r.Description,
        r.ReportType, r.Visibility, r.Definition, r.IsDefault, r.IsDefaultSettingsRecord, r.DisplayOrder,
        r.IsDeleted, r.CreatedOn, r.CreatedBy, r.ModifiedOn, r.ModifiedBy, r.ViewEditFormId,
        f.PublicId AS ViewEditFormPublicId
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE r.PublicId = @publicId
          AND r.IsDeleted = 0
        """;

    private const string GetVisibleReportSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE r.PublicId = @publicId
          AND r.IsDeleted = 0
          AND (
              r.Visibility = 'Shared'
              OR (r.Visibility = 'Personal' AND r.OwnerId = @userId)
              OR (r.Visibility IN ('MyRole', 'SpecificRoles', 'Role') AND EXISTS (
                  SELECT 1 FROM meta.AppRoleReport arr
                  JOIN meta.AppUser au ON au.AppRoleId = arr.AppRoleId
                  WHERE arr.ReportId = r.Id AND au.UserId = @userId AND au.IsDeleted = 0
              ))
          )
        """;

    private const string GetFirstVisibleReportByTableSql = $"""
        SELECT TOP 1 {SelectColumns}
        FROM meta.Report r
        JOIN meta.AppTable t ON t.Id = r.AppTableId
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE t.PublicId = @tablePublicId
          AND r.IsDeleted = 0
          AND r.IsDefaultSettingsRecord = 0
          AND t.IsDeleted = 0
          AND (
              r.Visibility = 'Shared'
              OR (r.Visibility = 'Personal' AND r.OwnerId = @userId)
              OR (r.Visibility IN ('MyRole', 'SpecificRoles', 'Role') AND EXISTS (
                  SELECT 1 FROM meta.AppRoleReport arr
                  JOIN meta.AppUser au ON au.AppRoleId = arr.AppRoleId
                  WHERE arr.ReportId = r.Id AND au.UserId = @userId AND au.IsDeleted = 0
              ))
          )
        ORDER BY r.DisplayOrder, r.Name
        """;

    private const string GetAppIdByPublicIdSql = """
        SELECT t.AppId
        FROM meta.Report r
        JOIN meta.AppTable t ON t.Id = r.AppTableId
        WHERE r.PublicId = @publicId AND r.IsDeleted = 0
        """;

    private const string ListByTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE r.AppTableId = (SELECT Id FROM meta.AppTable WHERE PublicId = @tablePublicId AND IsDeleted = 0)
          AND r.IsDeleted = 0
          AND r.IsDefaultSettingsRecord = 0
          AND (
              r.Visibility = 'Shared'
              OR (r.Visibility = 'Personal' AND r.OwnerId = @userId)
              OR (r.Visibility IN ('MyRole', 'SpecificRoles', 'Role') AND EXISTS (
                  SELECT 1 FROM meta.AppRoleReport arr
                  JOIN meta.AppUser au ON au.AppRoleId = arr.AppRoleId
                  WHERE arr.ReportId = r.Id AND au.UserId = @userId AND au.IsDeleted = 0
              ))
          )
        ORDER BY r.DisplayOrder, r.Name
        """;

    // Same scoping + role-based Visibility predicate as ListByTableSql, projected down to the grid's
    // slim columns and made searchable/sortable/paged. {0} = whitelisted "column direction" fragment,
    // built from a fixed C# switch (ResolveSortColumn) — never from raw user input.
    private const string ListByTablePagedSqlTemplate = """
        SELECT r.PublicId AS Id, r.Name, r.Description, r.ReportType, r.Visibility, r.IsDefault, r.CreatedOn
        FROM meta.Report r
        WHERE r.AppTableId = (SELECT Id FROM meta.AppTable WHERE PublicId = @tablePublicId AND IsDeleted = 0)
          AND r.IsDeleted = 0
          AND r.IsDefaultSettingsRecord = 0
          AND (@search IS NULL OR r.Name LIKE @search OR r.ReportType LIKE @search OR r.Visibility LIKE @search)
          AND (
              r.Visibility = 'Shared'
              OR (r.Visibility = 'Personal' AND r.OwnerId = @userId)
              OR (r.Visibility IN ('MyRole', 'SpecificRoles', 'Role') AND EXISTS (
                  SELECT 1 FROM meta.AppRoleReport arr
                  JOIN meta.AppUser au ON au.AppRoleId = arr.AppRoleId
                  WHERE arr.ReportId = r.Id AND au.UserId = @userId AND au.IsDeleted = 0
              ))
          )
        ORDER BY {0}
        OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
        """;

    private const string CountByTableFilteredSql = """
        SELECT COUNT(1)
        FROM meta.Report r
        WHERE r.AppTableId = (SELECT Id FROM meta.AppTable WHERE PublicId = @tablePublicId AND IsDeleted = 0)
          AND r.IsDeleted = 0
          AND r.IsDefaultSettingsRecord = 0
          AND (@search IS NULL OR r.Name LIKE @search)
          AND (
              r.Visibility = 'Shared'
              OR (r.Visibility = 'Personal' AND r.OwnerId = @userId)
              OR (r.Visibility IN ('MyRole', 'SpecificRoles', 'Role') AND EXISTS (
                  SELECT 1 FROM meta.AppRoleReport arr
                  JOIN meta.AppUser au ON au.AppRoleId = arr.AppRoleId
                  WHERE arr.ReportId = r.Id AND au.UserId = @userId AND au.IsDeleted = 0
              ))
          )
        """;

    private const string ListByAppSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        JOIN meta.AppTable t ON t.Id = r.AppTableId
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE t.AppId = @appId
          AND r.IsDeleted = 0
          AND r.IsDefaultSettingsRecord = 0
          AND (
              r.Visibility = 'Shared'
              OR (r.Visibility = 'Personal' AND r.OwnerId = @userId)
              OR (r.Visibility IN ('MyRole', 'SpecificRoles', 'Role') AND EXISTS (
                  SELECT 1 FROM meta.AppRoleReport arr
                  JOIN meta.AppUser au ON au.AppRoleId = arr.AppRoleId
                  WHERE arr.ReportId = r.Id AND au.UserId = @userId AND au.IsDeleted = 0
              ))
          )
        ORDER BY r.DisplayOrder, r.Name
        """;

    private const string ListAllByAppSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        JOIN meta.AppTable t ON t.Id = r.AppTableId
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE t.AppId = @appId
          AND r.IsDeleted = 0
          AND r.IsDefaultSettingsRecord = 0
        ORDER BY r.DisplayOrder, r.Name
        """;

    private const string GetDefaultByTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE r.AppTableId = (SELECT Id FROM meta.AppTable WHERE PublicId = @tablePublicId AND IsDeleted = 0)
          AND r.IsDefault = 1
          AND r.IsDeleted = 0
        """;

    // The hidden per-table row backing Default Report Settings — see IsDefaultSettingsRecord's
    // doc comment. Distinct from GetDefaultByTableSql above (IsDefault = 1), which is a different
    // report and a different concept.
    private const string GetDefaultSettingsRecordSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE r.AppTableId = (SELECT Id FROM meta.AppTable WHERE PublicId = @tablePublicId AND IsDeleted = 0)
          AND r.IsDefaultSettingsRecord = 1
          AND r.IsDeleted = 0
        """;

    private const string BelongsToTableSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM meta.Report r
            JOIN meta.AppTable t ON t.Id = r.AppTableId
            WHERE t.PublicId = @tablePublicId
              AND r.PublicId = @reportPublicId
              AND r.IsDeleted = 0
              AND t.IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.Report
            (AppTableId, OwnerId, Name, Description, ReportType, Visibility,
             Definition, IsDefault, IsDefaultSettingsRecord, DisplayOrder, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES
            (@appTableId, @ownerId, @name, @description, @reportType, @visibility,
             @definition, @isDefault, @isDefaultSettingsRecord, @displayOrder, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdateReportSql = """
        UPDATE meta.Report
        SET Name = @name, Description = @description, Visibility = @visibility,
            Definition = @definition, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string UnsetDefaultSql = """
        UPDATE meta.Report
        SET IsDefault = 0, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE AppTableId = (SELECT Id FROM meta.AppTable WHERE PublicId = @tablePublicId AND IsDeleted = 0)
          AND IsDeleted = 0
        """;

    private const string SetDefaultSql = """
        UPDATE meta.Report
        SET IsDefault = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE PublicId = @reportPublicId AND IsDeleted = 0
        """;

    private const string SoftDeleteReportSql = """
        UPDATE meta.Report
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string UpdateReportFormOverrideSql = """
        UPDATE r
        SET r.ViewEditFormId = (SELECT Id FROM meta.Form WHERE PublicId = @viewEditFormPublicId AND IsDeleted = 0),
            r.ModifiedOn = SYSUTCDATETIME(),
            r.ModifiedBy = @modifiedBy
        FROM meta.Report r
        WHERE r.PublicId = @reportPublicId AND r.IsDeleted = 0
        """;

    private const string ClearReportFormOverrideSql = """
        UPDATE r
        SET r.ViewEditFormId = NULL,
            r.ModifiedOn = SYSUTCDATETIME(),
            r.ModifiedBy = @modifiedBy
        FROM meta.Report r
        WHERE r.PublicId = @reportPublicId AND r.IsDeleted = 0
        """;

    public ReportRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<Report> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var report = await connection.QuerySingleOrDefaultAsync<Report>(
            new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
        return report ?? throw new NotFoundException("Report", publicId);
    }

    public async Task<Report?> GetVisibleReportAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Report>(
            new CommandDefinition(GetVisibleReportSql,
                new { publicId, userId = QueryContext.UserId },
                cancellationToken: ct));
    }

    public async Task<Report?> GetFirstVisibleReportByTableAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Report>(
            new CommandDefinition(GetFirstVisibleReportByTableSql,
                new { tablePublicId, userId = QueryContext.UserId },
                cancellationToken: ct));
    }

    public async Task<long> GetAppIdByPublicIdAsync(Guid reportPublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var appId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetAppIdByPublicIdSql, new { publicId = reportPublicId }, cancellationToken: ct));
        return appId ?? throw new NotFoundException("Report", reportPublicId);
    }

    public async Task<IReadOnlyList<Report>> ListByAppAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Report>(
            new CommandDefinition(ListByAppSql, new { appId, userId = QueryContext.UserId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<Report>> ListAllByAppAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Report>(
            new CommandDefinition(ListAllByAppSql,
                new { appId },
                cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<Report>> ListByTableAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Report>(
            new CommandDefinition(ListByTableSql, new { tablePublicId, userId = QueryContext.UserId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<ReportListItemDto>> ListByTablePagedAsync(
        Guid tablePublicId, int page, int pageSize, string? search, string sortBy, bool sortDesc, CancellationToken ct = default)
    {
        var column = ResolveSortColumn(sortBy);
        var sql = string.Format(ListByTablePagedSqlTemplate, $"{column} {(sortDesc ? "DESC" : "ASC")}, r.Id");

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<ReportListItemDto>(
            new CommandDefinition(sql, new
            {
                tablePublicId,
                userId = QueryContext.UserId,
                search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%",
                offset = (page - 1) * pageSize,
                pageSize
            }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> CountByTableAsync(Guid tablePublicId, string? search, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(CountByTableFilteredSql, new
            {
                tablePublicId,
                userId = QueryContext.UserId,
                search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%",
            }, cancellationToken: ct));
    }

    private static string ResolveSortColumn(string sortBy) => sortBy switch
    {
        "reportType" => "r.ReportType",
        "visibility" => "r.Visibility",
        "isDefault" => "r.IsDefault",
        "createdOn" => "r.CreatedOn",
        _ => "r.Name",
    };

    public async Task<Report?> GetDefaultByTableAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Report>(
            new CommandDefinition(GetDefaultByTableSql, new { tablePublicId }, cancellationToken: ct));
    }

    public async Task<Report?> GetDefaultSettingsRecordAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<Report>(
            new CommandDefinition(GetDefaultSettingsRecordSql, new { tablePublicId }, cancellationToken: ct));
    }

    public async Task<bool> BelongsToTableAsync(Guid tablePublicId, Guid reportPublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(BelongsToTableSql, new { tablePublicId, reportPublicId }, cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(Report report, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var row = await connection.QuerySingleAsync(
            new CommandDefinition(InsertSql, new
            {
                appTableId = report.AppTableId,
                ownerId = report.OwnerId,
                name = report.Name,
                description = report.Description,
                reportType = report.ReportType,
                visibility = report.Visibility,
                definition = report.Definition,
                isDefault = report.IsDefault,
                isDefaultSettingsRecord = report.IsDefaultSettingsRecord,
                displayOrder = report.DisplayOrder,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
        return ((long)row.Id, (Guid)row.PublicId);
    }

    public async Task<int> UpdateAsync(Guid publicId, string name, string? description,
        string visibility, string definition, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateReportSql,
                new { publicId, name, description, visibility, definition, modifiedBy = QueryContext.UserId },
                cancellationToken: ct));
    }

    public async Task SetDefaultAsync(Guid tablePublicId, Guid reportPublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(UnsetDefaultSql, new { tablePublicId, modifiedBy = QueryContext.UserId }, transaction: transaction, cancellationToken: ct));
            await connection.ExecuteAsync(
                new CommandDefinition(SetDefaultSql, new { reportPublicId, modifiedBy = QueryContext.UserId }, transaction: transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<int> DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteReportSql, new { publicId, deletedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task UpdateFormOverridesAsync(Guid tablePublicId, IEnumerable<(Guid ReportPublicId, Guid? ViewEditFormPublicId)> overrides, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var (reportPublicId, viewEditFormPublicId) in overrides)
            {
                var sql = viewEditFormPublicId.HasValue ? UpdateReportFormOverrideSql : ClearReportFormOverrideSql;
                await connection.ExecuteAsync(
                    new CommandDefinition(sql, new { reportPublicId, viewEditFormPublicId, modifiedBy = QueryContext.UserId }, transaction: transaction, cancellationToken: ct));
            }
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task SetReportRolesAsync(long reportId, IEnumerable<long> roleIds, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition("DELETE FROM meta.AppRoleReport WHERE ReportId = @reportId", new { reportId }, transaction: transaction, cancellationToken: ct));
            if (roleIds.Any())
            {
                var parameters = roleIds.Select(roleId => new { reportId, roleId }).ToList();
                await connection.ExecuteAsync(
                    new CommandDefinition("INSERT INTO meta.AppRoleReport (ReportId, AppRoleId) VALUES (@reportId, @roleId)", parameters, transaction: transaction, cancellationToken: ct));
            }
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<IReadOnlyList<long>> GetReportRoleIdsAsync(long reportId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<long>(
            new CommandDefinition("SELECT AppRoleId FROM meta.AppRoleReport WHERE ReportId = @reportId", new { reportId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<Guid>> GetReportRolePublicIdsAsync(long reportId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<Guid>(
            new CommandDefinition(@"
                SELECT ar.PublicId
                FROM meta.AppRoleReport arr
                JOIN meta.AppRole ar ON ar.Id = arr.AppRoleId
                WHERE arr.ReportId = @reportId", new { reportId }, cancellationToken: ct));
        return results.AsList();
    }

    // ── Grid Edit & Form Rules ───────────────────────────────────────────────────────────────────
    // "Applicable" rule = active, not deleted, with at least one action a client-side grid pre-check
    // can act on. Tested with EXISTS (not a DISTINCT join) so a rule with several such actions is
    // never expanded into duplicate rows that then need de-duplicating.

    private const string GridEditActionTypes = "'Require','PreventSave','Enable','Disable','ChangeValue','DisplayMessage'";

    private const string ApplicableRuleExists = $"""
        EXISTS (SELECT 1 FROM meta.FormRuleAction a WHERE a.FormRuleId = r.Id AND a.ActionType IN ({GridEditActionTypes}))
        """;

    private const string ListGridEditFormOptionsSql = $"""
        SELECT f.PublicId AS Id, f.Name,
               (SELECT COUNT(*) FROM meta.FormRule r
                 WHERE r.FormId = f.Id AND r.IsDeleted = 0 AND r.IsActive = 1 AND {ApplicableRuleExists}) AS RuleCount
        FROM meta.Form f
        WHERE f.AppTableId = @appTableId AND f.IsDeleted = 0
        ORDER BY f.DisplayOrder, f.Name
        """;

    private const string GetGridEditFormIdsSql = """
        SELECT f.PublicId
        FROM meta.ReportGridEditForm g
        JOIN meta.Report rep ON rep.Id = g.ReportId
        JOIN meta.Form f ON f.Id = g.FormId AND f.IsDeleted = 0
        WHERE rep.PublicId = @reportPublicId
        ORDER BY g.DisplayOrder
        """;

    // Stored rows (in their saved priority order) first; rules with no stored row — added to a
    // selected form since the last save, which apply automatically — after them, in form then rule order.
    private const string ListGridEditRuleStatesSql = $"""
        SELECT r.PublicId AS Id, r.Name AS RuleName, f.PublicId AS FormId, f.Name AS FormName,
               CAST(ISNULL(g.IsExcluded, 0) AS BIT) AS IsExcluded
        FROM meta.ReportGridEditForm rf
        JOIN meta.Report rep ON rep.Id = rf.ReportId
        JOIN meta.Form f ON f.Id = rf.FormId AND f.IsDeleted = 0
        JOIN meta.FormRule r ON r.FormId = f.Id AND r.IsDeleted = 0 AND r.IsActive = 1
        LEFT JOIN meta.ReportGridEditRule g ON g.ReportId = rf.ReportId AND g.FormRuleId = r.Id
        WHERE rep.PublicId = @reportPublicId AND {ApplicableRuleExists}
        ORDER BY CASE WHEN g.Id IS NULL THEN 1 ELSE 0 END, g.DisplayOrder, rf.DisplayOrder, r.DisplayOrder, r.Id
        """;

    private const string ListGridEditRulesForFormSql = $"""
        SELECT r.PublicId AS Id, r.Name AS RuleName, f.PublicId AS FormId, f.Name AS FormName
        FROM meta.FormRule r
        JOIN meta.Form f ON f.Id = r.FormId
        WHERE f.PublicId = @formPublicId AND f.AppTableId = @appTableId AND f.IsDeleted = 0
          AND r.IsDeleted = 0 AND r.IsActive = 1 AND {ApplicableRuleExists}
        ORDER BY r.DisplayOrder, r.Id
        """;

    private const string DeleteGridEditFormsSql = """
        DELETE g FROM meta.ReportGridEditForm g JOIN meta.Report r ON r.Id = g.ReportId WHERE r.PublicId = @reportPublicId
        """;

    private const string DeleteGridEditRulesSql = """
        DELETE g FROM meta.ReportGridEditRule g JOIN meta.Report r ON r.Id = g.ReportId WHERE r.PublicId = @reportPublicId
        """;

    // Both inserts join through the report's own table (f.AppTableId = rep.AppTableId) so a crafted
    // request can't attach another table's form or rule to this report.
    private const string InsertGridEditFormSql = """
        INSERT INTO meta.ReportGridEditForm (ReportId, FormId, DisplayOrder)
        SELECT rep.Id, f.Id, @displayOrder
        FROM meta.Report rep JOIN meta.Form f ON f.PublicId = @formPublicId AND f.AppTableId = rep.AppTableId AND f.IsDeleted = 0
        WHERE rep.PublicId = @reportPublicId
        """;

    private const string InsertGridEditRuleSql = """
        INSERT INTO meta.ReportGridEditRule (ReportId, FormRuleId, DisplayOrder, IsExcluded)
        SELECT rep.Id, fr.Id, @displayOrder, @isExcluded
        FROM meta.Report rep
        JOIN meta.FormRule fr ON fr.PublicId = @rulePublicId AND fr.IsDeleted = 0
        JOIN meta.Form f ON f.Id = fr.FormId AND f.AppTableId = rep.AppTableId
        WHERE rep.PublicId = @reportPublicId
        """;

    // One batch, one round trip: the ordered applied rules are staged once in a table variable (its
    // IDENTITY preserves the ORDER BY), then conditions, actions and each form's element->field map
    // are read for exactly those rules/forms.
    private const string GetGridEditRuntimeSql = $"""
        DECLARE @rules TABLE (Seq INT IDENTITY(1,1), RuleId BIGINT, FormId BIGINT);
        INSERT INTO @rules (RuleId, FormId)
        SELECT r.Id, r.FormId
        FROM meta.ReportGridEditForm rf
        JOIN meta.Report rep ON rep.Id = rf.ReportId
        JOIN meta.Form f ON f.Id = rf.FormId AND f.IsDeleted = 0
        JOIN meta.FormRule r ON r.FormId = f.Id AND r.IsDeleted = 0 AND r.IsActive = 1
        LEFT JOIN meta.ReportGridEditRule g ON g.ReportId = rf.ReportId AND g.FormRuleId = r.Id
        WHERE rep.PublicId = @reportPublicId AND ISNULL(g.IsExcluded, 0) = 0 AND {ApplicableRuleExists}
        ORDER BY CASE WHEN g.Id IS NULL THEN 1 ELSE 0 END, g.DisplayOrder, rf.DisplayOrder, r.DisplayOrder, r.Id;

        SELECT r.Id AS RuleId, t.FormId AS FormInternalId, r.PublicId, r.Name, f.PublicId AS FormPublicId,
               r.IsExpressionMode, r.ExpressionText, r.ConditionLogic
        FROM @rules t JOIN meta.FormRule r ON r.Id = t.RuleId JOIN meta.Form f ON f.Id = t.FormId
        ORDER BY t.Seq;

        SELECT c.FormRuleId, c.ConditionKind, c.AppFieldId, c.Operator, c.Value, c.ValueType, c.ValueFieldId, c.DisplayOrder
        FROM meta.FormRuleCondition c JOIN @rules t ON t.RuleId = c.FormRuleId
        ORDER BY c.FormRuleId, c.DisplayOrder;

        SELECT a.FormRuleId, a.ActionType, a.TargetType, a.TargetElementId, a.TargetSectionId, a.TargetBlockId,
               a.ActionValue, a.RunOnceOnActivation, a.IsExpressionValue, a.DisplayOrder
        FROM meta.FormRuleAction a JOIN @rules t ON t.RuleId = a.FormRuleId
        ORDER BY a.FormRuleId, a.DisplayOrder;

        SELECT s.FormId, e.Id AS ElementId, e.AppFieldId
        FROM meta.FormElement e JOIN meta.FormSection s ON s.Id = e.FormSectionId
        WHERE s.FormId IN (SELECT FormId FROM @rules) AND e.AppFieldId IS NOT NULL;
        """;

    public async Task<IReadOnlyList<GridEditFormOption>> ListGridEditFormOptionsAsync(long appTableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<GridEditFormOption>(
            new CommandDefinition(ListGridEditFormOptionsSql, new { appTableId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<Guid>> GetGridEditFormIdsAsync(Guid reportPublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<Guid>(
            new CommandDefinition(GetGridEditFormIdsSql, new { reportPublicId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<GridEditRuleState>> ListGridEditRuleStatesAsync(Guid reportPublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<GridEditRuleState>(
            new CommandDefinition(ListGridEditRuleStatesSql, new { reportPublicId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<GridEditRuleItem>> ListGridEditRulesForFormAsync(long appTableId, Guid formPublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<GridEditRuleItem>(
            new CommandDefinition(ListGridEditRulesForFormSql, new { appTableId, formPublicId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SetGridEditConfigAsync(Guid reportPublicId, IReadOnlyList<Guid> formIds,
        IReadOnlyList<Guid> appliedRuleIds, IReadOnlyList<Guid> excludedRuleIds, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(DeleteGridEditRulesSql, new { reportPublicId }, transaction: transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(DeleteGridEditFormsSql, new { reportPublicId }, transaction: transaction, cancellationToken: ct));

            // Dapper runs a statement once per element of an enumerable parameter, on the same
            // connection/transaction — one call per list instead of a hand-written loop.
            if (formIds.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(InsertGridEditFormSql,
                    formIds.Select((id, i) => new { reportPublicId, formPublicId = id, displayOrder = i + 1 }).ToList(),
                    transaction: transaction, cancellationToken: ct));

            var ruleRows = appliedRuleIds.Select((id, i) => new { reportPublicId, rulePublicId = id, displayOrder = i + 1, isExcluded = false })
                .Concat(excludedRuleIds.Select(id => new { reportPublicId, rulePublicId = id, displayOrder = 0, isExcluded = true }))
                .ToList();
            if (ruleRows.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(InsertGridEditRuleSql, ruleRows, transaction: transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<IReadOnlyList<GridEditRuntimeRule>> GetGridEditRuntimeAsync(Guid reportPublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        using var multi = await connection.QueryMultipleAsync(
            new CommandDefinition(GetGridEditRuntimeSql, new { reportPublicId }, cancellationToken: ct));

        var ruleRows   = (await multi.ReadAsync<GridEditRuntimeRuleRow>()).ToList();
        var conditions = (await multi.ReadAsync<FormRuleCondition>()).ToLookup(c => c.FormRuleId);
        var actions    = (await multi.ReadAsync<FormRuleAction>()).ToLookup(a => a.FormRuleId);
        var elements   = (await multi.ReadAsync<GridEditElementRow>()).ToLookup(e => e.FormId);

        return ruleRows.Select(r => new GridEditRuntimeRule
        {
            Id = r.PublicId,
            Name = r.Name,
            FormId = r.FormPublicId,
            IsExpressionMode = r.IsExpressionMode,
            ExpressionText = r.ExpressionText,
            ConditionLogic = r.ConditionLogic,
            Conditions = conditions[r.RuleId].ToList(),
            Actions = actions[r.RuleId].ToList(),
            ElementFieldMap = elements[r.FormInternalId].ToDictionary(e => e.ElementId, e => (long)e.AppFieldId),
        }).ToList();
    }

    private sealed class GridEditRuntimeRuleRow
    {
        public long RuleId { get; set; }
        public long FormInternalId { get; set; }
        public Guid PublicId { get; set; }
        public string Name { get; set; } = string.Empty;
        public Guid FormPublicId { get; set; }
        public bool IsExpressionMode { get; set; }
        public string? ExpressionText { get; set; }
        public string ConditionLogic { get; set; } = "all";
    }

    private sealed class GridEditElementRow
    {
        public long FormId { get; set; }
        public long ElementId { get; set; }
        public int AppFieldId { get; set; }
    }

    public async Task<Dictionary<long, List<long>>> GetAppRoleReportsMapAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<(long ReportId, long AppRoleId)>(
            new CommandDefinition("""
                SELECT arr.ReportId, arr.AppRoleId
                FROM meta.AppRoleReport arr
                JOIN meta.Report r ON r.Id = arr.ReportId
                JOIN meta.AppTable t ON t.Id = r.AppTableId
                WHERE t.AppId = @appId AND r.IsDeleted = 0
                """, new { appId }, cancellationToken: ct));
        return results.GroupBy(x => x.ReportId)
                      .ToDictionary(g => g.Key, g => g.Select(x => x.AppRoleId).ToList());
    }
}
