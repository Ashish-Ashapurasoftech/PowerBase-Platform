using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Forms;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms;
using PowerBase.Application.Forms.Commands.CreateForm;
using PowerBase.Application.Forms.Commands.CreateFormRule;
using PowerBase.Application.Forms.Commands.DeleteForm;
using PowerBase.Application.Forms.Commands.DeleteFormRule;
using PowerBase.Application.Forms.Commands.DuplicateForm;
using PowerBase.Application.Forms.Commands.DuplicateFormRule;
using PowerBase.Application.Forms.Commands.ReorderFormRules;
using PowerBase.Application.Forms.Commands.SaveFormLayout;
using PowerBase.Application.Forms.Commands.SaveFormRule;
using PowerBase.Application.Forms.Commands.ToggleFormRule;
using PowerBase.Application.Forms.Commands.UpdateFormSettings;
using PowerBase.Application.Forms.Queries.GetForm;
using PowerBase.Application.Forms.Queries.GetFormLayout;
using PowerBase.Application.Forms.Queries.GetFormRule;
using PowerBase.Application.Forms.Queries.ListFormRules;
using PowerBase.Application.Forms.Queries.ListForms;
using PowerBase.Application.Forms.Commands.SetDefaultForm;
using PowerBase.Application.Forms.Commands.UpdateRoleFormOverrides;
using PowerBase.Application.Forms.Queries.GetRoleFormOverrides;
using PowerBase.Application.Forms.Queries.ResolveForm;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
public class FormsController : ControllerBase
{
    private readonly CreateFormCommandHandler _createFormHandler;
    private readonly UpdateFormSettingsCommandHandler _updateSettingsHandler;
    private readonly SaveFormLayoutCommandHandler _saveLayoutHandler;
    private readonly DeleteFormCommandHandler _deleteFormHandler;
    private readonly DuplicateFormCommandHandler _duplicateFormHandler;
    private readonly CreateFormRuleCommandHandler _createRuleHandler;
    private readonly SaveFormRuleCommandHandler _saveRuleHandler;
    private readonly DeleteFormRuleCommandHandler _deleteRuleHandler;
    private readonly DuplicateFormRuleCommandHandler _duplicateRuleHandler;
    private readonly ReorderFormRulesCommandHandler _reorderRulesHandler;
    private readonly ToggleFormRuleCommandHandler _toggleRuleHandler;
    private readonly GetFormQueryHandler _getFormHandler;
    private readonly ListFormsQueryHandler _listFormsHandler;
    private readonly GetFormLayoutQueryHandler _getLayoutHandler;
    private readonly ListFormRulesQueryHandler _listRulesHandler;
    private readonly GetFormRuleQueryHandler _getRuleHandler;
    private readonly SetDefaultFormCommandHandler _setDefaultFormHandler;
    private readonly GetRoleFormOverridesQueryHandler _getRoleFormOverridesHandler;
    private readonly UpdateRoleFormOverridesCommandHandler _updateRoleFormOverridesHandler;
    private readonly ResolveFormQueryHandler _resolveFormHandler;

    public FormsController(
        CreateFormCommandHandler createFormHandler,
        UpdateFormSettingsCommandHandler updateSettingsHandler,
        SaveFormLayoutCommandHandler saveLayoutHandler,
        DeleteFormCommandHandler deleteFormHandler,
        DuplicateFormCommandHandler duplicateFormHandler,
        CreateFormRuleCommandHandler createRuleHandler,
        SaveFormRuleCommandHandler saveRuleHandler,
        DeleteFormRuleCommandHandler deleteRuleHandler,
        DuplicateFormRuleCommandHandler duplicateRuleHandler,
        ReorderFormRulesCommandHandler reorderRulesHandler,
        ToggleFormRuleCommandHandler toggleRuleHandler,
        GetFormQueryHandler getFormHandler,
        ListFormsQueryHandler listFormsHandler,
        GetFormLayoutQueryHandler getLayoutHandler,
        ListFormRulesQueryHandler listRulesHandler,
        GetFormRuleQueryHandler getRuleHandler,
        SetDefaultFormCommandHandler setDefaultFormHandler,
        GetRoleFormOverridesQueryHandler getRoleFormOverridesHandler,
        UpdateRoleFormOverridesCommandHandler updateRoleFormOverridesHandler,
        ResolveFormQueryHandler resolveFormHandler)
    {
        _createFormHandler = createFormHandler;
        _updateSettingsHandler = updateSettingsHandler;
        _saveLayoutHandler = saveLayoutHandler;
        _deleteFormHandler = deleteFormHandler;
        _duplicateFormHandler = duplicateFormHandler;
        _createRuleHandler = createRuleHandler;
        _saveRuleHandler = saveRuleHandler;
        _deleteRuleHandler = deleteRuleHandler;
        _duplicateRuleHandler = duplicateRuleHandler;
        _reorderRulesHandler = reorderRulesHandler;
        _toggleRuleHandler = toggleRuleHandler;
        _getFormHandler = getFormHandler;
        _listFormsHandler = listFormsHandler;
        _getLayoutHandler = getLayoutHandler;
        _listRulesHandler = listRulesHandler;
        _getRuleHandler = getRuleHandler;
        _setDefaultFormHandler = setDefaultFormHandler;
        _getRoleFormOverridesHandler = getRoleFormOverridesHandler;
        _updateRoleFormOverridesHandler = updateRoleFormOverridesHandler;
        _resolveFormHandler = resolveFormHandler;
    }

    // ── Forms ────────────────────────────────────────────────────────────────

    /// <summary>List all forms for a table.</summary>
    [HttpGet("tables/{tableId:guid}/forms")]
    [RequireAppPermission(PermissionCodes.FormsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<FormListItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListForms(Guid tableId, CancellationToken ct)
    {
        var results = await _listFormsHandler.HandleAsync(new ListFormsQuery(tableId), ct);
        var items = results.Select(MapToListItem).ToList();
        return Ok(new ApiResponse<IReadOnlyList<FormListItemResponse>>(items));
    }

    /// <summary>Get form settings (defaults & role overrides).</summary>
    [HttpGet("tables/{tableId:guid}/forms/settings")]
    [RequireAppPermission(PermissionCodes.FormsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<DefaultFormSettingsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFormSettings(Guid tableId, CancellationToken ct)
    {
        var forms = await _listFormsHandler.HandleAsync(new ListFormsQuery(tableId), ct);
        var overrides = await _getRoleFormOverridesHandler.HandleAsync(new GetRoleFormOverridesQuery(tableId), ct);
        var everyoneOverride = overrides.FirstOrDefault(o => o.RoleId == null);
        var roleOverrides = overrides.Where(o => o.RoleId != null).ToList();

        var response = new DefaultFormSettingsResponse
        {
            EveryoneEditFormId = everyoneOverride?.EditFormId ?? forms.FirstOrDefault(f => f.IsDefault)?.Id ?? Guid.Empty,
            EveryoneAddFormId = everyoneOverride?.AddFormId ?? forms.FirstOrDefault(f => f.IsDefault)?.Id ?? Guid.Empty,
            Roles = roleOverrides.Select(o => new RoleFormOverrideResponse
            {
                RoleId = o.RoleId!.Value,
                RoleName = o.RoleName ?? "",
                EditFormId = o.EditFormId,
                AddFormId = o.AddFormId
            }).ToList(),
            Forms = forms.Select(MapToListItem).ToList()
        };

        return Ok(new ApiResponse<DefaultFormSettingsResponse>(response));
    }

    /// <summary>Update form settings (defaults & role overrides).</summary>
    [HttpPut("tables/{tableId:guid}/forms/settings")]
    [RequireAppPermission(PermissionCodes.FormsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateFormSettings(Guid tableId, [FromBody] UpdateDefaultFormSettingsRequest request, CancellationToken ct)
    {
        var overrides = new List<RoleFormOverrideCommandDto>
        {
            new RoleFormOverrideCommandDto(null, request.EveryoneEditFormId, request.EveryoneAddFormId)
        };
        overrides.AddRange(request.RoleOverrides.Select(o => new RoleFormOverrideCommandDto(o.RoleId, o.EditFormId, o.AddFormId)));

        await _updateRoleFormOverridesHandler.HandleAsync(new UpdateRoleFormOverridesCommand(tableId, overrides), ct);
        return NoContent();
    }

    /// <summary>Resolve which form to use for Add/Edit.</summary>
    [HttpGet("tables/{tableId:guid}/forms/resolve")]
    [RequireAppMember(AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<FormDetailResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResolveForm(Guid tableId, [FromQuery] string mode, [FromQuery] Guid? reportId, CancellationToken ct)
    {
        var form = await _resolveFormHandler.HandleAsync(new ResolveFormQuery(tableId, mode, reportId), ct);
        if (form == null) return NotFound();
        return Ok(new ApiResponse<FormDetailResponse>(MapToDetail(form)));
    }

    /// <summary>Set a form as the default form for the table.</summary>
    [HttpPost("forms/{publicId:guid}/set-default")]
    [RequireAppPermission(PermissionCodes.FormsUpdate, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetDefaultForm(Guid publicId, [FromQuery] Guid tableId, CancellationToken ct)
    {
        await _setDefaultFormHandler.HandleAsync(new SetDefaultFormCommand(tableId, publicId), ct);
        return NoContent();
    }

    /// <summary>Create a new form for a table.</summary>
    [HttpPost("tables/{tableId:guid}/forms")]
    [RequireAppPermission(PermissionCodes.FormsCreate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<FormDetailResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateForm(Guid tableId, [FromBody] CreateFormRequest request, CancellationToken ct)
    {
        var result = await _createFormHandler.HandleAsync(new CreateFormCommand(tableId, request.Name), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<FormDetailResponse>(MapToDetail(result)));
    }

    /// <summary>Get a single form.</summary>
    [HttpGet("forms/{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.FormsRead, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(typeof(ApiResponse<FormDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForm(Guid publicId, CancellationToken ct)
    {
        var result = await _getFormHandler.HandleAsync(new GetFormQuery(publicId), ct);
        return Ok(new ApiResponse<FormDetailResponse>(MapToDetail(result)));
    }

    /// <summary>Update form settings (name, auto-add, show built-in, save options).</summary>
    [HttpPatch("forms/{publicId:guid}/settings")]
    [RequireAppPermission(PermissionCodes.FormsUpdate, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateSettings(Guid publicId, [FromBody] UpdateFormSettingsRequest request, CancellationToken ct)
    {
        var rowVersion = Convert.FromBase64String(request.RowVersion);
        await _updateSettingsHandler.HandleAsync(new UpdateFormSettingsCommand(
            publicId, request.Name, request.AutoAddNewFields, request.ShowBuiltInFields,
            request.SaveOptions, rowVersion), ct);
        return NoContent();
    }

    /// <summary>Soft-delete a form. Default forms cannot be deleted.</summary>
    [HttpDelete("forms/{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.FormsDelete, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteForm(Guid publicId, CancellationToken ct)
    {
        await _deleteFormHandler.HandleAsync(new DeleteFormCommand(publicId), ct);
        return NoContent();
    }

    /// <summary>Duplicate a form (and its layout) under a new name.</summary>
    [HttpPost("forms/{publicId:guid}/duplicate")]
    [RequireAppPermission(PermissionCodes.FormsCreate, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(typeof(ApiResponse<FormDetailResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DuplicateForm(Guid publicId, [FromBody] DuplicateFormRequest request, CancellationToken ct)
    {
        var result = await _duplicateFormHandler.HandleAsync(new DuplicateFormCommand(publicId, request.Name), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<FormDetailResponse>(MapToDetail(result)));
    }

    // ── Form Layout ──────────────────────────────────────────────────────────

    /// <summary>Get the full section/element layout of a form.</summary>
    [HttpGet("forms/{publicId:guid}/layout")]
    [RequireAppPermission(PermissionCodes.FormsRead, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(typeof(ApiResponse<FormLayoutResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLayout(Guid publicId, CancellationToken ct)
    {
        var result = await _getLayoutHandler.HandleAsync(new GetFormLayoutQuery(publicId), ct);
        return Ok(new ApiResponse<FormLayoutResponse>(MapToLayoutResponse(result)));
    }

    /// <summary>Replace the entire form layout (sections + elements) atomically.</summary>
    [HttpPut("forms/{publicId:guid}/layout")]
    [RequireAppPermission(PermissionCodes.FormsUpdate, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SaveLayout(Guid publicId, [FromBody] SaveFormLayoutRequest request, CancellationToken ct)
    {
        var sections = request.Sections.Select(s => new FormSectionLayout(
            s.PublicId,
            s.Name,
            s.IsCollapsed,
            s.Blocks.Select(b => new FormBlockLayout(
                b.PublicId,
                b.Heading,
                b.BackgroundColor,
                b.Width,
                b.Elements.Select(e => new FormElementLayout(
                    e.PublicId,
                    e.AppFieldId,
                    e.ElementType,
                    e.ElementContent,
                    e.LabelMode,
                    e.CustomLabel,
                    e.ShowOnAdd,
                    e.ShowOnEdit,
                    e.ShowOnView,
                    e.WidthMode,
                    e.WidthValue,
                    e.HelpTextOverride,
                    e.IsReadOnly,
                    e.IsRequired,
                    e.DisplayAs)).ToList()
            )).ToList()
        )).ToList();

        await _saveLayoutHandler.HandleAsync(new SaveFormLayoutCommand(publicId, sections), ct);
        return NoContent();
    }

    // ── Form Rules ───────────────────────────────────────────────────────────

    /// <summary>List all rules for a form.</summary>
    [HttpGet("forms/{publicId:guid}/rules")]
    [RequireAppPermission(PermissionCodes.FormsRead, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<FormRuleListItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListRules(Guid publicId, CancellationToken ct)
    {
        var results = await _listRulesHandler.HandleAsync(new ListFormRulesQuery(publicId), ct);
        var items = results.Select(MapToRuleListItem).ToList();
        return Ok(new ApiResponse<IReadOnlyList<FormRuleListItemResponse>>(items));
    }

    /// <summary>Create a new (empty) form rule.</summary>
    [HttpPost("forms/{publicId:guid}/rules")]
    [RequireAppPermission(PermissionCodes.FormsRulesManage, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(typeof(ApiResponse<FormRuleDetailResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateRule(Guid publicId, [FromBody] CreateFormRuleRequest request, CancellationToken ct)
    {
        var result = await _createRuleHandler.HandleAsync(
            new CreateFormRuleCommand(publicId, request.Name), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<FormRuleDetailResponse>(MapToRuleDetail(result)));
    }

    /// <summary>Get a single form rule with all conditions and actions.</summary>
    [HttpGet("forms/rules/{ruleId:guid}")]
    [RequireAppPermission(PermissionCodes.FormsRead, AppAccessResolver.ByFormRulePublicId)]
    [ProducesResponseType(typeof(ApiResponse<FormRuleDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRule(Guid ruleId, CancellationToken ct)
    {
        var result = await _getRuleHandler.HandleAsync(new GetFormRuleQuery(ruleId), ct);
        return Ok(new ApiResponse<FormRuleDetailResponse>(MapToRuleDetail(result)));
    }

    /// <summary>Replace a rule's body (header + conditions + actions) atomically.</summary>
    [HttpPut("forms/rules/{ruleId:guid}")]
    [RequireAppPermission(PermissionCodes.FormsRulesManage, AppAccessResolver.ByFormRulePublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SaveRule(Guid ruleId, [FromBody] SaveFormRuleRequest request, CancellationToken ct)
    {
        var rowVersion = Convert.FromBase64String(request.RowVersion);
        var conditions = request.Conditions.Select(c =>
            new FormRuleConditionSpec(c.AppFieldId, c.Operator, c.Value, c.ValueType, c.ValueFieldId, c.DisplayOrder)).ToList();
        var actions = request.Actions.Select(a =>
            new FormRuleActionSpec(a.ActionType, a.TargetType, a.TargetElementId, a.TargetSectionId, a.TargetBlockId, a.ActionValue, a.DisplayOrder)).ToList();

        await _saveRuleHandler.HandleAsync(new SaveFormRuleCommand(
            ruleId, request.Name, request.Description, request.Tags, request.IsActive,
            request.RunTrigger, request.ConditionLogic, request.IsExpressionMode,
            request.ExpressionText, conditions, actions, rowVersion), ct);
        return NoContent();
    }

    /// <summary>Soft-delete a form rule.</summary>
    [HttpDelete("forms/rules/{ruleId:guid}")]
    [RequireAppPermission(PermissionCodes.FormsRulesManage, AppAccessResolver.ByFormRulePublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRule(Guid ruleId, CancellationToken ct)
    {
        await _deleteRuleHandler.HandleAsync(new DeleteFormRuleCommand(ruleId), ct);
        return NoContent();
    }

    /// <summary>Duplicate a form rule.</summary>
    [HttpPost("forms/rules/{ruleId:guid}/duplicate")]
    [RequireAppPermission(PermissionCodes.FormsRulesManage, AppAccessResolver.ByFormRulePublicId)]
    [ProducesResponseType(typeof(ApiResponse<FormRuleDetailResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DuplicateRule(Guid ruleId, [FromBody] DuplicateFormRuleRequest request, CancellationToken ct)
    {
        var result = await _duplicateRuleHandler.HandleAsync(new DuplicateFormRuleCommand(ruleId, request.Name), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<FormRuleDetailResponse>(MapToRuleDetail(result)));
    }

    /// <summary>Toggle a rule's active/inactive state.</summary>
    [HttpPatch("forms/rules/{ruleId:guid}/toggle")]
    [RequireAppPermission(PermissionCodes.FormsRulesManage, AppAccessResolver.ByFormRulePublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleRule(Guid ruleId, [FromBody] ToggleFormRuleRequest request, CancellationToken ct)
    {
        await _toggleRuleHandler.HandleAsync(new ToggleFormRuleCommand(ruleId, request.IsActive), ct);
        return NoContent();
    }

    /// <summary>Reorder rules within a form by supplying the ordered list of rule GUIDs.</summary>
    [HttpPut("forms/{publicId:guid}/rules/reorder")]
    [RequireAppPermission(PermissionCodes.FormsRulesManage, AppAccessResolver.ByFormPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReorderRules(Guid publicId, [FromBody] ReorderFormRulesRequest request, CancellationToken ct)
    {
        await _reorderRulesHandler.HandleAsync(new ReorderFormRulesCommand(publicId, request.OrderedRuleIds), ct);
        return NoContent();
    }

    // ── Mappers ──────────────────────────────────────────────────────────────

    private static FormListItemResponse MapToListItem(FormDetail f) => new()
    {
        Id           = f.Id,
        Name         = f.Name,
        IsDefault    = f.IsDefault,
        DisplayOrder = f.DisplayOrder,
        CreatedOn    = f.CreatedOn,
    };

    private static FormDetailResponse MapToDetail(FormDetail f) => new()
    {
        Id                = f.Id,
        Name              = f.Name,
        IsDefault         = f.IsDefault,
        AutoAddNewFields  = f.AutoAddNewFields,
        ShowBuiltInFields = f.ShowBuiltInFields,
        SaveOptions       = f.SaveOptions.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
        DisplayOrder      = f.DisplayOrder,
        RowVersion        = Convert.ToBase64String(f.RowVersion),
        CreatedOn         = f.CreatedOn,
    };

    private static FormLayoutResponse MapToLayoutResponse(FormLayoutDetail layout) => new()
    {
        FormId   = layout.FormId,
        Sections = layout.Sections.Select(s => new FormSectionResponse
        {
            DbId         = s.DbId,
            Id           = s.Id,
            Name         = s.Name,
            ColumnCount  = s.ColumnCount,
            ColumnWidths = s.ColumnWidths,
            IsCollapsed  = s.IsCollapsed,
            DisplayOrder = s.DisplayOrder,
            Blocks       = s.Blocks.Select(b => new FormBlockResponse
            {
                DbId            = b.DbId,
                Id              = b.Id,
                Heading         = b.Heading,
                BackgroundColor = b.BackgroundColor,
                Width           = b.Width,
                DisplayOrder    = b.DisplayOrder,
                Elements        = b.Elements.Select(e => new FormElementResponse
                {
                    DbId             = e.DbId,
                    Id               = e.Id,
                    AppFieldId       = e.AppFieldId,
                    ElementType      = e.ElementType,
                    ElementContent   = e.ElementContent,
                    LabelMode        = e.LabelMode,
                    CustomLabel      = e.CustomLabel,
                    ShowOnAdd        = e.ShowOnAdd,
                    ShowOnEdit       = e.ShowOnEdit,
                    ShowOnView       = e.ShowOnView,
                    WidthMode        = e.WidthMode,
                    WidthValue       = e.WidthValue,
                    HelpTextOverride = e.HelpTextOverride,
                    IsReadOnly       = e.IsReadOnly,
                    IsRequired       = e.IsRequired,
                    DisplayAs        = e.DisplayAs,
                    DisplayOrder     = e.DisplayOrder,
                }).ToList(),
            }).ToList(),
        }).ToList(),
    };

    private static FormRuleListItemResponse MapToRuleListItem(FormRuleDetail r) => new()
    {
        Id           = r.Id,
        Name         = r.Name,
        Tags         = r.Tags,
        IsActive     = r.IsActive,
        ConditionCount = r.Conditions.Count,
        ActionCount    = r.Actions.Count,
        DisplayOrder = r.DisplayOrder,
        CreatedOn    = r.CreatedOn,
    };

    private static FormRuleDetailResponse MapToRuleDetail(FormRuleDetail r) => new()
    {
        Id               = r.Id,
        Name             = r.Name,
        Description      = r.Description,
        Tags             = r.Tags,
        IsActive         = r.IsActive,
        RunTrigger       = r.RunTrigger,
        ConditionLogic   = r.ConditionLogic,
        IsExpressionMode = r.IsExpressionMode,
        ExpressionText   = r.ExpressionText,
        DisplayOrder     = r.DisplayOrder,
        RowVersion       = Convert.ToBase64String(r.RowVersion),
        CreatedOn        = r.CreatedOn,
        Conditions       = r.Conditions.Select(c => new FormRuleConditionResponse
        {
            AppFieldId   = c.AppFieldId,
            Operator     = c.Operator,
            Value        = c.Value,
            ValueType    = c.ValueType,
            ValueFieldId = c.ValueFieldId,
            DisplayOrder = c.DisplayOrder,
        }).ToList(),
        Actions = r.Actions.Select(a => new FormRuleActionResponse
        {
            ActionType      = a.ActionType,
            TargetType      = a.TargetType,
            TargetElementId = a.TargetElementId,
            TargetSectionId = a.TargetSectionId,
            TargetBlockId   = a.TargetBlockId,
            ActionValue     = a.ActionValue,
            DisplayOrder    = a.DisplayOrder,
        }).ToList(),
    };
}

