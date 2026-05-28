using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class FormRuleRepository : BaseRepository, IFormRuleRepository
{
    public FormRuleRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    // -----------------------------------------------------------------------
    // Selects
    // -----------------------------------------------------------------------

    private const string RuleSelectColumns = """
        Id, PublicId, TenantId, FormId, Name, Description, Tags, IsActive,
        IsExpressionMode, ExpressionText, RunTrigger, ConditionLogic, DisplayOrder,
        IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, RowVersion
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {RuleSelectColumns}
        FROM meta.FormRule
        WHERE TenantId = @tenantId
          AND PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string GetAppIdByPublicIdSql = """
        SELECT t.AppId
        FROM meta.FormRule r
        JOIN meta.Form f      ON f.Id = r.FormId
        JOIN meta.AppTable t  ON t.Id = f.AppTableId
        WHERE r.TenantId = @tenantId
          AND r.PublicId = @publicId
          AND r.IsDeleted = 0
        """;

    private const string ListByFormRulesSql = $"""
        SELECT {RuleSelectColumns}
        FROM meta.FormRule
        WHERE TenantId = @tenantId
          AND FormId   = @formId
          AND IsDeleted = 0
        ORDER BY DisplayOrder, Id
        """;

    private const string ListConditionsSql = """
        SELECT c.Id, c.FormRuleId, c.AppFieldId, c.Operator, c.Value, c.DisplayOrder
        FROM meta.FormRuleCondition c
        JOIN meta.FormRule r ON r.Id = c.FormRuleId
        WHERE r.TenantId = @tenantId
          AND r.FormId   = @formId
          AND r.IsDeleted = 0
        ORDER BY c.FormRuleId, c.DisplayOrder
        """;

    private const string ListActionsSql = """
        SELECT a.Id, a.FormRuleId, a.ActionType, a.TargetType,
               a.TargetElementId, a.TargetSectionId, a.DisplayOrder
        FROM meta.FormRuleAction a
        JOIN meta.FormRule r ON r.Id = a.FormRuleId
        WHERE r.TenantId = @tenantId
          AND r.FormId   = @formId
          AND r.IsDeleted = 0
        ORDER BY a.FormRuleId, a.DisplayOrder
        """;

    private const string InsertRuleSql = """
        INSERT INTO meta.FormRule
            (TenantId, FormId, Name, Description, Tags, IsActive, IsExpressionMode,
             ExpressionText, RunTrigger, ConditionLogic, DisplayOrder, IsDeleted,
             CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES
            (@tenantId, @formId, @name, @description, @tags, @isActive, @isExpressionMode,
             @expressionText, @runTrigger, @conditionLogic, @displayOrder, 0,
             SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdateRuleHeaderSql = """
        UPDATE meta.FormRule
        SET Name             = @name,
            Description      = @description,
            Tags             = @tags,
            IsActive         = @isActive,
            IsExpressionMode = @isExpressionMode,
            ExpressionText   = @expressionText,
            RunTrigger       = @runTrigger,
            ConditionLogic   = @conditionLogic,
            ModifiedOn       = SYSUTCDATETIME(),
            ModifiedBy       = @modifiedBy
        WHERE TenantId  = @tenantId
          AND PublicId  = @publicId
          AND IsDeleted = 0
          AND RowVersion = @rowVersion
        """;

    private const string DeleteConditionsSql = """
        DELETE FROM meta.FormRuleCondition WHERE FormRuleId = @formRuleId
        """;

    private const string DeleteActionsSql = """
        DELETE FROM meta.FormRuleAction WHERE FormRuleId = @formRuleId
        """;

    private const string InsertConditionSql = """
        INSERT INTO meta.FormRuleCondition (FormRuleId, AppFieldId, Operator, Value, DisplayOrder)
        VALUES (@formRuleId, @appFieldId, @operator, @value, @displayOrder)
        """;

    private const string InsertActionSql = """
        INSERT INTO meta.FormRuleAction (FormRuleId, ActionType, TargetType, TargetElementId, TargetSectionId, DisplayOrder)
        VALUES (@formRuleId, @actionType, @targetType, @targetElementId, @targetSectionId, @displayOrder)
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.FormRule
        SET IsDeleted = 1,
            DeletedOn = SYSUTCDATETIME(),
            DeletedBy = @deletedBy
        WHERE TenantId  = @tenantId
          AND PublicId  = @publicId
          AND IsDeleted = 0
        """;

    private const string SetActiveSql = """
        UPDATE meta.FormRule
        SET IsActive   = @isActive,
            ModifiedOn = SYSUTCDATETIME(),
            ModifiedBy = @modifiedBy
        WHERE TenantId  = @tenantId
          AND PublicId  = @publicId
          AND IsDeleted = 0
        """;

    private const string GetRuleIdSql = """
        SELECT Id FROM meta.FormRule
        WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
        """;

    private const string UpdateDisplayOrderSql = """
        UPDATE meta.FormRule
        SET DisplayOrder = @displayOrder
        WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
        """;

    // -----------------------------------------------------------------------
    // Public methods
    // -----------------------------------------------------------------------

    public async Task<FormRule> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        var rule = await conn.QuerySingleOrDefaultAsync<FormRule>(
            new CommandDefinition(GetByPublicIdSql,
                new { tenantId = QueryContext.TenantId, publicId },
                cancellationToken: ct));
        if (rule is null) throw new NotFoundException("FormRule", publicId);

        var conditions = await conn.QueryAsync<FormRuleCondition>(
            new CommandDefinition(
                "SELECT Id, FormRuleId, AppFieldId, Operator, Value, DisplayOrder FROM meta.FormRuleCondition WHERE FormRuleId = @ruleId ORDER BY DisplayOrder",
                new { ruleId = rule.Id }, cancellationToken: ct));
        rule.Conditions = conditions.ToList();

        var actions = await conn.QueryAsync<FormRuleAction>(
            new CommandDefinition(
                "SELECT Id, FormRuleId, ActionType, TargetType, TargetElementId, TargetSectionId, DisplayOrder FROM meta.FormRuleAction WHERE FormRuleId = @ruleId ORDER BY DisplayOrder",
                new { ruleId = rule.Id }, cancellationToken: ct));
        rule.Actions = actions.ToList();

        return rule;
    }

    public async Task<long> GetAppIdByPublicIdAsync(Guid rulePublicId, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        var appId = await conn.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(GetAppIdByPublicIdSql,
                new { tenantId = QueryContext.TenantId, publicId = rulePublicId },
                cancellationToken: ct));
        return appId ?? throw new NotFoundException("FormRule", rulePublicId);
    }

    public async Task<IReadOnlyList<FormRule>> ListByFormAsync(long formId, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        using var multi = await conn.QueryMultipleAsync(
            new CommandDefinition(
                $"{ListByFormRulesSql};\n{ListConditionsSql};\n{ListActionsSql}",
                new { tenantId = QueryContext.TenantId, formId },
                cancellationToken: ct));

        var rules      = (await multi.ReadAsync<FormRule>()).ToList();
        var conditions = (await multi.ReadAsync<FormRuleCondition>()).ToList();
        var actions    = (await multi.ReadAsync<FormRuleAction>()).ToList();

        var ruleMap = rules.ToDictionary(r => r.Id);
        foreach (var c in conditions)
        {
            if (ruleMap.TryGetValue(c.FormRuleId, out var rule)) rule.Conditions.Add(c);
        }
        foreach (var a in actions)
        {
            if (ruleMap.TryGetValue(a.FormRuleId, out var rule)) rule.Actions.Add(a);
        }
        return rules;
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(FormRule rule, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        var maxOrder = await conn.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                "SELECT ISNULL(MAX(DisplayOrder),0) FROM meta.FormRule WHERE TenantId=@tenantId AND FormId=@formId AND IsDeleted=0",
                new { tenantId = rule.TenantId, formId = rule.FormId },
                cancellationToken: ct)) ?? 0;

        var row = await conn.QuerySingleAsync(
            new CommandDefinition(InsertRuleSql, new
            {
                tenantId         = rule.TenantId,
                formId           = rule.FormId,
                name             = rule.Name,
                description      = rule.Description,
                tags             = rule.Tags,
                isActive         = rule.IsActive,
                isExpressionMode = rule.IsExpressionMode,
                expressionText   = rule.ExpressionText,
                runTrigger       = rule.RunTrigger,
                conditionLogic   = rule.ConditionLogic,
                displayOrder     = maxOrder + 1,
                createdBy        = rule.CreatedBy,
            }, cancellationToken: ct));
        return ((long)row.Id, (Guid)row.PublicId);
    }

    public async Task SaveRuleBodyAsync(Guid publicId, string name, string? description, string? tags,
        bool isActive, string runTrigger, string conditionLogic, bool isExpressionMode,
        string? expressionText, IReadOnlyList<FormRuleCondition> conditions,
        IReadOnlyList<FormRuleAction> actions, byte[] rowVersion, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = await conn.ExecuteAsync(
            new CommandDefinition(UpdateRuleHeaderSql, new
            {
                tenantId         = QueryContext.TenantId,
                publicId,
                name,
                description,
                tags,
                isActive,
                isExpressionMode,
                expressionText,
                runTrigger,
                conditionLogic,
                modifiedBy       = QueryContext.UserId,
                rowVersion,
            }, tx, cancellationToken: ct));

        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            throw new ConcurrencyException("FormRule");
        }

        var ruleId = await conn.QuerySingleAsync<long>(
            new CommandDefinition(GetRuleIdSql,
                new { tenantId = QueryContext.TenantId, publicId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(DeleteConditionsSql, new { formRuleId = ruleId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(DeleteActionsSql,    new { formRuleId = ruleId }, tx, cancellationToken: ct));

        foreach (var c in conditions)
        {
            await conn.ExecuteAsync(new CommandDefinition(InsertConditionSql, new
            {
                formRuleId   = ruleId,
                appFieldId   = c.AppFieldId,
                @operator    = c.Operator,
                value        = c.Value,
                displayOrder = c.DisplayOrder,
            }, tx, cancellationToken: ct));
        }

        foreach (var a in actions)
        {
            await conn.ExecuteAsync(new CommandDefinition(InsertActionSql, new
            {
                formRuleId      = ruleId,
                actionType      = a.ActionType,
                targetType      = a.TargetType,
                targetElementId = a.TargetElementId,
                targetSectionId = a.TargetSectionId,
                displayOrder    = a.DisplayOrder,
            }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<int> DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        return await conn.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql,
                new { tenantId = QueryContext.TenantId, publicId, deletedBy = QueryContext.UserId },
                cancellationToken: ct));
    }

    public async Task ReorderAsync(long formId, IReadOnlyList<Guid> orderedPublicIds, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        for (var i = 0; i < orderedPublicIds.Count; i++)
        {
            await conn.ExecuteAsync(new CommandDefinition(UpdateDisplayOrderSql, new
            {
                tenantId     = QueryContext.TenantId,
                publicId     = orderedPublicIds[i],
                displayOrder = i + 1,
            }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<int> SetActiveAsync(Guid publicId, bool isActive, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        return await conn.ExecuteAsync(
            new CommandDefinition(SetActiveSql,
                new { tenantId = QueryContext.TenantId, publicId, isActive, modifiedBy = QueryContext.UserId },
                cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> DuplicateAsync(Guid sourcePublicId, string newName,
        long tenantId, long userId, CancellationToken ct = default)
    {
        var source = await GetByPublicIdAsync(sourcePublicId, ct);

        var newRule = new FormRule
        {
            TenantId         = tenantId,
            FormId           = source.FormId,
            Name             = newName,
            Description      = source.Description,
            Tags             = source.Tags,
            IsActive         = false,
            IsExpressionMode = source.IsExpressionMode,
            ExpressionText   = source.ExpressionText,
            RunTrigger       = source.RunTrigger,
            ConditionLogic   = source.ConditionLogic,
            CreatedBy        = userId,
        };

        var (newId, newPublicId) = await CreateAsync(newRule, ct);

        if (source.Conditions.Count > 0 || source.Actions.Count > 0)
        {
            await using var conn = ConnectionFactory.Create();
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            foreach (var c in source.Conditions)
            {
                await conn.ExecuteAsync(new CommandDefinition(InsertConditionSql, new
                {
                    formRuleId   = newId,
                    appFieldId   = c.AppFieldId,
                    @operator    = c.Operator,
                    value        = c.Value,
                    displayOrder = c.DisplayOrder,
                }, tx, cancellationToken: ct));
            }
            foreach (var a in source.Actions)
            {
                await conn.ExecuteAsync(new CommandDefinition(InsertActionSql, new
                {
                    formRuleId      = newId,
                    actionType      = a.ActionType,
                    targetType      = a.TargetType,
                    targetElementId = a.TargetElementId,
                    targetSectionId = a.TargetSectionId,
                    displayOrder    = a.DisplayOrder,
                }, tx, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }

        return (newId, newPublicId);
    }
}
