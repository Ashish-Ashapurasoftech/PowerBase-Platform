using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Tables.Commands.UpdateTable;

public class UpdateTableResult
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? SingularLabel { get; init; }
    public string? Icon { get; init; }
    public bool IsShowInBar { get; init; }
    public DateTime CreatedOn { get; init; }
}

public class UpdateTableCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAuditRepository _auditRepo;
    private readonly FormulaEngine _engine;

    public UpdateTableCommandHandler(
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IAppAccessService appAccessService,
        IAuditRepository auditRepo, FormulaEngine engine)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _appAccessService = appAccessService;
        _auditRepo = auditRepo;
        _engine = engine;
    }

    public async Task<UpdateTableResult> HandleAsync(UpdateTableCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });
        if (command.Name.Length > 200)
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name must be 200 characters or fewer."] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        if (table == null)
            throw new NotFoundException("Table", command.TablePublicId);

        var changes = new List<string>();
        if (table.Name != command.Name)
            changes.Add($"Name to '{command.Name}'");
        if (table.SingularLabel != command.SingularLabel)
            changes.Add($"Singular Label to '{command.SingularLabel}'");
        if (table.PluralLabel != command.PluralLabel)
            changes.Add($"Plural Label to '{command.PluralLabel}'");
        if (table.Description != command.Description)
            changes.Add("Description");
        if (table.Icon != command.Icon)
            changes.Add($"Icon to '{command.Icon}'");
        if (table.DefaultRecordPickerField1Id != command.DefaultRecordPickerField1Id)
            changes.Add("Default Record Picker Field 1");
        if (table.DefaultRecordPickerField2Id != command.DefaultRecordPickerField2Id)
            changes.Add("Default Record Picker Field 2");
        if (table.DefaultRecordPickerField3Id != command.DefaultRecordPickerField3Id)
            changes.Add("Default Record Picker Field 3");
        if (command.IsShowInBar.HasValue && table.IsShowInBar != command.IsShowInBar.Value)
            changes.Add($"Show In Bar to '{command.IsShowInBar.Value}'");
        if (table.CustomDataRule != command.CustomDataRule)
            changes.Add("Custom Data Rule");
        if (table.IsCustomDataRuleEnabled != command.CustomDataRuleEnabled)
            changes.Add($"Custom Data Rules turned {(command.CustomDataRuleEnabled ? "on" : "off")}");

        // The Custom Data Rule is the authoritative save-time gate on every record write to this
        // table, so an invalid formula must never persist while it's live — validate it here (not
        // just rely on the Advanced Settings page's live /formula/validate call) before touching
        // the row. While the "Turn custom data rules on?" toggle is off, skip validation entirely
        // — the rule isn't enforced yet, so an admin can save a formula mid-draft.
        if (command.CustomDataRuleEnabled && !string.IsNullOrWhiteSpace(command.CustomDataRule))
        {
            var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
            var schema = new AppFieldSchema(fields);
            var aliasSchema = await AppTableAliasSchema.BuildAsync(_tableRepo, table.AppId, ct);
            var compiled = _engine.Compile(command.CustomDataRule, schema, FormulaType.Text, aliasSchema);
            if (compiled.HasErrors)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["customDataRule"] = ["Invalid custom data rule formula. Please check the formula syntax."],
                });
        }

        var affected = await _tableRepo.UpdateAsync(
            command.TablePublicId, command.Name,
            command.SingularLabel, command.PluralLabel,
            command.Description, command.Icon,
            command.DefaultRecordPickerField1Id,
            command.DefaultRecordPickerField2Id,
            command.DefaultRecordPickerField3Id,
            command.IsShowInBar,
            ct);

        if (table.CustomDataRule != command.CustomDataRule || table.IsCustomDataRuleEnabled != command.CustomDataRuleEnabled)
            await _tableRepo.UpdateCustomDataRuleAsync(command.TablePublicId, command.CustomDataRule, command.CustomDataRuleEnabled, ct);

        if (affected > 0 && changes.Count > 0)
        {
            var logMessage = $"Table updated: {string.Join(", ", changes)}";
            await _auditRepo.LogActivityAsync(
                AuditActions.Updated, AuditEntityTypes.AppTable, command.TablePublicId.ToString(), logMessage, appId: table.AppId, ct: ct);
        }

        return new UpdateTableResult
        {
            PublicId = table.PublicId,
            Name = command.Name,
            SingularLabel = command.SingularLabel,
            Icon = command.Icon,
            IsShowInBar = command.IsShowInBar ?? table.IsShowInBar,
            CreatedOn = table.CreatedOn,
        };
    }
}
