using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class FormRepository : BaseRepository, IFormRepository
{
    public FormRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    // -----------------------------------------------------------------------
    // Selects
    // -----------------------------------------------------------------------

    private const string SelectColumns = """
        Id, PublicId, TenantId, AppTableId, Name, IsDefault, AutoAddNewFields,
        ShowBuiltInFields, SaveOptions, DisplayOrder, IsDeleted,
        CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.Form
        WHERE TenantId = @tenantId
          AND PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string GetAppIdByPublicIdSql = """
        SELECT t.AppId
        FROM meta.Form f
        JOIN meta.AppTable t ON t.Id = f.AppTableId
        WHERE f.TenantId = @tenantId
          AND f.PublicId = @publicId
          AND f.IsDeleted = 0
        """;

    private const string ListByTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.Form
        WHERE TenantId = @tenantId
          AND AppTableId = (
              SELECT Id FROM meta.AppTable
              WHERE PublicId = @tablePublicId AND TenantId = @tenantId AND IsDeleted = 0)
          AND IsDeleted = 0
        ORDER BY DisplayOrder, Name
        """;

    private const string InsertSql = """
        INSERT INTO meta.Form
            (TenantId, AppTableId, Name, IsDefault, AutoAddNewFields, ShowBuiltInFields,
             SaveOptions, DisplayOrder, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES
            (@tenantId, @appTableId, @name, @isDefault, @autoAddNewFields, @showBuiltInFields,
             @saveOptions, @displayOrder, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdateSettingsSql = """
        UPDATE meta.Form
        SET Name             = @name,
            AutoAddNewFields = @autoAddNewFields,
            ShowBuiltInFields = @showBuiltInFields,
            SaveOptions      = @saveOptions,
            ModifiedOn       = SYSUTCDATETIME(),
            ModifiedBy       = @modifiedBy
        WHERE TenantId  = @tenantId
          AND PublicId  = @publicId
          AND IsDeleted = 0
          AND RowVersion = @rowVersion
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.Form
        SET IsDeleted = 1,
            DeletedOn = SYSUTCDATETIME(),
            DeletedBy = @deletedBy
        WHERE TenantId  = @tenantId
          AND PublicId  = @publicId
          AND IsDeleted = 0
        """;

    private const string UnsetDefaultFormSql = """
        UPDATE meta.Form
        SET IsDefault = 0, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE AppTableId = (SELECT Id FROM meta.AppTable WHERE PublicId = @tablePublicId AND TenantId = @tenantId AND IsDeleted = 0)
          AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string SetDefaultFormSql = """
        UPDATE meta.Form
        SET IsDefault = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE TenantId = @tenantId AND PublicId = @formPublicId AND IsDeleted = 0
        """;

    private const string GetRoleFormOverridesSql = """
        SELECT 
            r.PublicId AS RolePublicId, 
            ef.PublicId AS EditFormPublicId, 
            af.PublicId AS AddFormPublicId
        FROM meta.AppRoleTableFormOverride o
        JOIN meta.AppTable t ON t.Id = o.AppTableId
        LEFT JOIN meta.AppRole r ON r.Id = o.AppRoleId
        LEFT JOIN meta.Form ef ON ef.Id = o.EditFormId
        LEFT JOIN meta.Form af ON af.Id = o.AddFormId
        WHERE o.TenantId = @tenantId
          AND t.PublicId = @tablePublicId
          AND t.IsDeleted = 0
        """;

    private const string DeleteRoleFormOverridesSql = """
        DELETE o
        FROM meta.AppRoleTableFormOverride o
        JOIN meta.AppTable t ON t.Id = o.AppTableId
        WHERE o.TenantId = @tenantId
          AND t.PublicId = @tablePublicId
        """;

    private const string InsertRoleFormOverrideSql = """
        INSERT INTO meta.AppRoleTableFormOverride (TenantId, AppTableId, AppRoleId, EditFormId, AddFormId, CreatedBy)
        VALUES (
            @tenantId,
            (SELECT Id FROM meta.AppTable WHERE PublicId = @tablePublicId AND TenantId = @tenantId AND IsDeleted = 0),
            CASE WHEN @rolePublicId IS NULL THEN NULL ELSE (SELECT Id FROM meta.AppRole WHERE PublicId = @rolePublicId AND TenantId = @tenantId AND IsDeleted = 0) END,
            CASE WHEN @editFormPublicId IS NULL THEN NULL ELSE (SELECT Id FROM meta.Form WHERE PublicId = @editFormPublicId AND TenantId = @tenantId AND IsDeleted = 0) END,
            CASE WHEN @addFormPublicId IS NULL THEN NULL ELSE (SELECT Id FROM meta.Form WHERE PublicId = @addFormPublicId AND TenantId = @tenantId AND IsDeleted = 0) END,
            @createdBy
        )
        """;


    private const string GetSectionsSql = """
        SELECT Id, PublicId, TenantId, FormId, Name, ColumnCount, ColumnWidths, IsCollapsed, DisplayOrder
        FROM meta.FormSection
        WHERE FormId = @formId
        ORDER BY DisplayOrder
        """;

    private const string GetBlocksSql = """
        SELECT fsb.Id, fsb.PublicId, fsb.TenantId, fsb.FormSectionId,
               fsb.Heading, fsb.BackgroundColor, fsb.Width, fsb.DisplayOrder
        FROM meta.FormSectionBlock fsb
        JOIN meta.FormSection fs ON fs.Id = fsb.FormSectionId
        WHERE fs.FormId = @formId
        ORDER BY fsb.FormSectionId, fsb.DisplayOrder
        """;

    private const string GetElementsSql = """
        SELECT fe.Id, fe.PublicId, fe.TenantId, fe.FormSectionId, fe.FormSectionBlockId,
               fe.AppFieldId, fe.ElementType, fe.ElementContent,
               fe.LabelMode, fe.CustomLabel, fe.ShowOnAdd, fe.ShowOnEdit, fe.ShowOnView,
               fe.WidthMode, fe.WidthValue, fe.HelpTextOverride, fe.IsReadOnly,
               fe.IsRequired, fe.DisplayAs, fe.DisplayOrder
        FROM meta.FormElement fe
        JOIN meta.FormSection fs ON fs.Id = fe.FormSectionId
        WHERE fs.FormId = @formId
        ORDER BY fe.FormSectionId, fe.FormSectionBlockId, fe.DisplayOrder
        """;

    private const string DeleteSectionsSql = "DELETE FROM meta.FormSection WHERE FormId = @formId";

    private const string NullRuleActionTargetsSql = """
        UPDATE ra
        SET ra.TargetElementId = NULL,
            ra.TargetSectionId = NULL
        FROM meta.FormRuleAction ra
        JOIN meta.FormRule r ON r.Id = ra.FormRuleId
        WHERE r.FormId = @formId
        """;

    private const string InsertSectionSql = """
        INSERT INTO meta.FormSection (TenantId, FormId, Name, ColumnCount, ColumnWidths, IsCollapsed, DisplayOrder)
        OUTPUT INSERTED.Id
        VALUES (@tenantId, @formId, @name, @columnCount, @columnWidths, @isCollapsed, @displayOrder)
        """;

    private const string InsertBlockSql = """
        INSERT INTO meta.FormSectionBlock (TenantId, FormSectionId, Heading, BackgroundColor, Width, DisplayOrder)
        OUTPUT INSERTED.Id
        VALUES (@tenantId, @formSectionId, @heading, @backgroundColor, @width, @displayOrder)
        """;

    private const string InsertElementSql = """
        INSERT INTO meta.FormElement
            (TenantId, FormSectionId, FormSectionBlockId, AppFieldId, ElementType, ElementContent,
             LabelMode, CustomLabel, ShowOnAdd, ShowOnEdit, ShowOnView,
             WidthMode, WidthValue, HelpTextOverride, IsReadOnly, IsRequired, DisplayAs, DisplayOrder)
        VALUES
            (@tenantId, @formSectionId, @formSectionBlockId, @appFieldId, @elementType, @elementContent,
             @labelMode, @customLabel, @showOnAdd, @showOnEdit, @showOnView,
             @widthMode, @widthValue, @helpTextOverride, @isReadOnly, @isRequired, @displayAs, @displayOrder)
        """;

    // AppendField uses the last section's last block (or falls back to section-only for legacy forms)
    private const string AppendFieldSql = """
        DECLARE @lastSectionId BIGINT = (
            SELECT TOP 1 Id FROM meta.FormSection
            WHERE FormId = @formId
            ORDER BY DisplayOrder DESC
        );
        IF @lastSectionId IS NOT NULL
        BEGIN
            DECLARE @lastBlockId BIGINT = (
                SELECT TOP 1 Id FROM meta.FormSectionBlock
                WHERE FormSectionId = @lastSectionId
                ORDER BY DisplayOrder DESC
            );
            DECLARE @nextOrder INT = (
                SELECT ISNULL(MAX(DisplayOrder), 0) + 1
                FROM meta.FormElement
                WHERE FormSectionId = @lastSectionId
                  AND (FormSectionBlockId = @lastBlockId OR (@lastBlockId IS NULL AND FormSectionBlockId IS NULL))
            );
            INSERT INTO meta.FormElement
                (TenantId, FormSectionId, FormSectionBlockId, AppFieldId, LabelMode,
                 ShowOnAdd, ShowOnEdit, ShowOnView, WidthMode, IsReadOnly, IsRequired, DisplayOrder)
            VALUES
                (@tenantId, @lastSectionId, @lastBlockId, @fieldId, 'Default',
                 1, 1, 1, 'Auto', 0, 0, @nextOrder);
        END
        """;

    // -----------------------------------------------------------------------
    // Public methods
    // -----------------------------------------------------------------------

    public async Task<Form> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        var form = await conn.QuerySingleOrDefaultAsync<Form>(
            new CommandDefinition(GetByPublicIdSql,
                new { tenantId = QueryContext.TenantId, publicId },
                cancellationToken: ct));
        return form ?? throw new NotFoundException("Form", publicId);
    }

    public async Task<long> GetAppIdByPublicIdAsync(Guid formPublicId, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        var appId = await conn.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(GetAppIdByPublicIdSql,
                new { tenantId = QueryContext.TenantId, publicId = formPublicId },
                cancellationToken: ct));
        return appId ?? throw new NotFoundException("Form", formPublicId);
    }

    public async Task<IReadOnlyList<Form>> ListByTableAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        var forms = await conn.QueryAsync<Form>(
            new CommandDefinition(ListByTableSql,
                new { tenantId = QueryContext.TenantId, tablePublicId },
                cancellationToken: ct));
        return forms.ToList();
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(Form form, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        var row = await conn.QuerySingleAsync(
            new CommandDefinition(InsertSql, new
            {
                tenantId          = form.TenantId,
                appTableId        = form.AppTableId,
                name              = form.Name,
                isDefault         = form.IsDefault,
                autoAddNewFields  = form.AutoAddNewFields,
                showBuiltInFields = form.ShowBuiltInFields,
                saveOptions       = form.SaveOptions,
                displayOrder      = form.DisplayOrder,
                createdBy         = form.CreatedBy,
            }, cancellationToken: ct));
        return ((long)row.Id, (Guid)row.PublicId);
    }

    public async Task<int> UpdateSettingsAsync(Guid publicId, string name, bool autoAddNewFields,
        bool showBuiltInFields, string saveOptions, byte[] rowVersion, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        var rows = await conn.ExecuteAsync(
            new CommandDefinition(UpdateSettingsSql, new
            {
                tenantId          = QueryContext.TenantId,
                publicId,
                name,
                autoAddNewFields,
                showBuiltInFields,
                saveOptions,
                modifiedBy        = QueryContext.UserId,
                rowVersion,
            }, cancellationToken: ct));
        if (rows == 0) throw new ConcurrencyException("Form");
        return rows;
    }

    public async Task<int> DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        return await conn.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql,
                new { tenantId = QueryContext.TenantId, publicId, deletedBy = QueryContext.UserId },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FormSection>> GetLayoutAsync(long formId, CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        using var multi = await conn.QueryMultipleAsync(
            new CommandDefinition(
                $"{GetSectionsSql};\n{GetBlocksSql};\n{GetElementsSql}",
                new { formId },
                cancellationToken: ct));

        var sections = (await multi.ReadAsync<FormSection>()).ToList();
        var blocks   = (await multi.ReadAsync<FormSectionBlock>()).ToList();
        var elements = (await multi.ReadAsync<FormElement>()).ToList();

        var sectionMap = sections.ToDictionary(s => s.Id);
        var blockMap   = blocks.ToDictionary(b => b.Id);

        // Attach blocks to sections
        foreach (var block in blocks)
        {
            if (sectionMap.TryGetValue(block.FormSectionId, out var section))
                section.Blocks.Add(block);
        }

        // Attach elements to their block (primary) or synthesize a default block (backward compat)
        foreach (var el in elements)
        {
            if (el.FormSectionBlockId.HasValue && blockMap.TryGetValue(el.FormSectionBlockId.Value, out var block))
            {
                block.Elements.Add(el);
            }
            else if (sectionMap.TryGetValue(el.FormSectionId, out var section))
            {
                // Legacy element with no block — synthesize a default block on the fly
                if (section.Blocks.Count == 0)
                {
                    section.Blocks.Add(new FormSectionBlock
                    {
                        Id            = 0,
                        PublicId      = Guid.Empty,
                        TenantId      = section.TenantId,
                        FormSectionId = section.Id,
                        DisplayOrder  = 1,
                    });
                }
                section.Blocks[0].Elements.Add(el);
            }
        }

        return sections;
    }

    public async Task SaveLayoutAsync(long formId, long tenantId, IReadOnlyList<FormSection> sections,
        CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            new CommandDefinition(NullRuleActionTargetsSql, new { formId }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(
            new CommandDefinition(DeleteSectionsSql, new { formId }, tx, cancellationToken: ct));

        for (var si = 0; si < sections.Count; si++)
        {
            var section = sections[si];
            var sectionId = await conn.QuerySingleAsync<long>(
                new CommandDefinition(InsertSectionSql, new
                {
                    tenantId,
                    formId,
                    name         = section.Name,
                    columnCount  = section.Blocks.Count,
                    columnWidths = (string?)null,          // widths now live on blocks
                    isCollapsed  = section.IsCollapsed,
                    displayOrder = si + 1,
                }, tx, cancellationToken: ct));

            for (var bi = 0; bi < section.Blocks.Count; bi++)
            {
                var block = section.Blocks[bi];
                var blockId = await conn.QuerySingleAsync<long>(
                    new CommandDefinition(InsertBlockSql, new
                    {
                        tenantId,
                        formSectionId   = sectionId,
                        heading         = block.Heading,
                        backgroundColor = block.BackgroundColor,
                        width           = block.Width,
                        displayOrder    = bi + 1,
                    }, tx, cancellationToken: ct));

                for (var ei = 0; ei < block.Elements.Count; ei++)
                {
                    var el = block.Elements[ei];
                    await conn.ExecuteAsync(
                        new CommandDefinition(InsertElementSql, new
                        {
                            tenantId,
                            formSectionId        = sectionId,
                            formSectionBlockId   = blockId,
                            appFieldId           = el.AppFieldId,
                            elementType          = el.ElementType,
                            elementContent       = el.ElementContent,
                            labelMode            = el.LabelMode,
                            customLabel          = el.CustomLabel,
                            showOnAdd            = el.ShowOnAdd,
                            showOnEdit           = el.ShowOnEdit,
                            showOnView           = el.ShowOnView,
                            widthMode            = el.WidthMode,
                            widthValue           = el.WidthValue,
                            helpTextOverride     = el.HelpTextOverride,
                            isReadOnly           = el.IsReadOnly,
                            isRequired           = el.IsRequired,
                            displayAs            = el.DisplayAs,
                            displayOrder         = ei + 1,
                        }, tx, cancellationToken: ct));
                }
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task AppendFieldToLastSectionAsync(long formId, long fieldId, long tenantId,
        CancellationToken ct = default)
    {
        await using var conn = ConnectionFactory.Create();
        await conn.ExecuteAsync(
            new CommandDefinition(AppendFieldSql,
                new { formId, fieldId, tenantId },
                cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> DuplicateAsync(Guid sourcePublicId, string newName,
        long tenantId, long userId, CancellationToken ct = default)
    {
        var source = await GetByPublicIdAsync(sourcePublicId, ct);
        var sourceLayout = await GetLayoutAsync(source.Id, ct);

        var newForm = new Form
        {
            TenantId          = tenantId,
            AppTableId        = source.AppTableId,
            Name              = newName,
            IsDefault         = false,
            AutoAddNewFields  = source.AutoAddNewFields,
            ShowBuiltInFields = source.ShowBuiltInFields,
            SaveOptions       = source.SaveOptions,
            DisplayOrder      = source.DisplayOrder + 1,
            CreatedBy         = userId,
        };

        var (newId, newPublicId) = await CreateAsync(newForm, ct);
        await SaveLayoutAsync(newId, tenantId, sourceLayout, ct);
        return (newId, newPublicId);
    }

    public async Task SetDefaultAsync(Guid tablePublicId, Guid formPublicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(UnsetDefaultFormSql,
                    new { tenantId = QueryContext.TenantId, tablePublicId, modifiedBy = QueryContext.UserId },
                    transaction: transaction, cancellationToken: ct));

            await connection.ExecuteAsync(
                new CommandDefinition(SetDefaultFormSql,
                    new { tenantId = QueryContext.TenantId, formPublicId, modifiedBy = QueryContext.UserId },
                    transaction: transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<(Guid? RolePublicId, Guid? EditFormPublicId, Guid? AddFormPublicId)>> GetRoleFormOverridesAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var overrides = await connection.QueryAsync<(Guid? RolePublicId, Guid? EditFormPublicId, Guid? AddFormPublicId)>(
            new CommandDefinition(GetRoleFormOverridesSql,
                new { tenantId = QueryContext.TenantId, tablePublicId },
                cancellationToken: ct));
        return overrides.ToList();
    }

    public async Task UpdateRoleFormOverridesAsync(Guid tablePublicId, IEnumerable<(Guid? RolePublicId, Guid? EditFormPublicId, Guid? AddFormPublicId)> overrides, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(DeleteRoleFormOverridesSql,
                    new { tenantId = QueryContext.TenantId, tablePublicId },
                    transaction: transaction, cancellationToken: ct));

            foreach (var o in overrides)
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(InsertRoleFormOverrideSql,
                        new 
                        { 
                            tenantId = QueryContext.TenantId, 
                            tablePublicId, 
                            rolePublicId = o.RolePublicId,
                            editFormPublicId = o.EditFormPublicId,
                            addFormPublicId = o.AddFormPublicId,
                            createdBy = QueryContext.UserId 
                        },
                        transaction: transaction, cancellationToken: ct));
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
