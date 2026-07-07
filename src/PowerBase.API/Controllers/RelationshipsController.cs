using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Relationships;
using PowerBase.Application.Relationships;
using PowerBase.Application.Relationships.Commands.AddLookupFields;
using PowerBase.Application.Relationships.Commands.AddSummaryField;
using PowerBase.Application.Relationships.Commands.CreateRelationship;
using PowerBase.Application.Relationships.Commands.DeleteRelationship;
using PowerBase.Application.Relationships.Commands.RemoveRelationshipField;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
public class RelationshipsController : ControllerBase
{
    private readonly CreateRelationshipCommandHandler _createHandler;
    private readonly DeleteRelationshipCommandHandler _deleteHandler;
    private readonly RelationshipQueriesHandler _queries;
    private readonly GetParentOptionsQueryHandler _parentOptions;
    private readonly AddLookupFieldsCommandHandler _addLookups;
    private readonly AddSummaryFieldCommandHandler _addSummary;
    private readonly RemoveRelationshipFieldCommandHandler _removeField;

    public RelationshipsController(
        CreateRelationshipCommandHandler createHandler,
        DeleteRelationshipCommandHandler deleteHandler,
        RelationshipQueriesHandler queries,
        GetParentOptionsQueryHandler parentOptions,
        AddLookupFieldsCommandHandler addLookups,
        AddSummaryFieldCommandHandler addSummary,
        RemoveRelationshipFieldCommandHandler removeField)
    {
        _createHandler = createHandler;
        _deleteHandler = deleteHandler;
        _queries = queries;
        _parentOptions = parentOptions;
        _addLookups = addLookups;
        _addSummary = addSummary;
        _removeField = removeField;
    }

    /// <summary>Create a one-to-many relationship (provisions the reference, lookup and summary fields).</summary>
    [HttpPost("apps/{appId:guid}/relationships")]
    [RequireAppPermission(PermissionCodes.FieldsCreate, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<RelationshipDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid appId, [FromBody] CreateRelationshipRequest request, CancellationToken ct)
    {
        var command = new CreateRelationshipCommand(
            appId,
            request.ParentTablePublicId,
            request.ChildTablePublicId,
            request.ReferenceFieldName,
            request.ReferenceFieldLabel,
            request.IsReferenceRequired,
            request.Lookups.Select(l => new CreateLookupSpec(l.SourceFid, l.Name, l.Label)).ToList(),
            request.Summaries.Select(s => new CreateSummarySpec(s.Name, s.Label, s.Function, s.TargetFid)).ToList(),
            request.ReferenceFieldFid);
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<RelationshipDto>(result));
    }

    /// <summary>List all relationships in an app.</summary>
    [HttpGet("apps/{appId:guid}/relationships")]
    [RequireAppPermission(PermissionCodes.FieldsRead, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiListResponse<RelationshipDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListByApp(Guid appId, CancellationToken ct)
    {
        var items = await _queries.ByAppAsync(appId, ct);
        return Ok(new ApiListResponse<RelationshipDto>(items, items.Count, 1, items.Count));
    }

    /// <summary>List relationships where the table is the parent or the child.</summary>
    [HttpGet("tables/{tableId:guid}/relationships")]
    [RequireAppPermission(PermissionCodes.FieldsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiListResponse<RelationshipDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListByTable(Guid tableId, CancellationToken ct)
    {
        var items = await _queries.ByTableAsync(tableId, ct);
        return Ok(new ApiListResponse<RelationshipDto>(items, items.Count, 1, items.Count));
    }

    /// <summary>Get one relationship with its participating fields (drives the relationship detail page).</summary>
    [HttpGet("apps/{appId:guid}/relationships/{id:guid}")]
    [RequireAppPermission(PermissionCodes.FieldsRead, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<RelationshipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid appId, Guid id, CancellationToken ct)
    {
        var dto = await _queries.GetAsync(id, ct);
        return Ok(new ApiResponse<RelationshipDto>(dto));
    }

    /// <summary>Add lookup fields to an existing relationship's child table.</summary>
    [HttpPost("apps/{appId:guid}/relationships/{id:guid}/lookups")]
    [RequireAppPermission(PermissionCodes.FieldsCreate, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<RelationshipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddLookups(Guid appId, Guid id, [FromBody] AddLookupFieldsRequest request, CancellationToken ct)
    {
        var command = new AddLookupFieldsCommand(id,
            request.Lookups.Select(l => new AddLookupSpec(l.SourceFid, l.Name, l.Label)).ToList());
        var result = await _addLookups.HandleAsync(command, ct);
        return Ok(new ApiResponse<RelationshipDto>(result));
    }

    /// <summary>Add a summary field to an existing relationship's parent table.</summary>
    [HttpPost("apps/{appId:guid}/relationships/{id:guid}/summaries")]
    [RequireAppPermission(PermissionCodes.FieldsCreate, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<RelationshipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddSummary(Guid appId, Guid id, [FromBody] AddSummaryFieldRequest request, CancellationToken ct)
    {
        var command = new AddSummaryFieldCommand(id, request.Name, request.Label, request.Function, request.TargetFid, request.MatchingCriteria);
        var result = await _addSummary.HandleAsync(command, ct);
        return Ok(new ApiResponse<RelationshipDto>(result));
    }

    /// <summary>Remove a single lookup or summary field from a relationship.</summary>
    [HttpDelete("apps/{appId:guid}/relationships/{id:guid}/fields/{fieldId:guid}")]
    [RequireAppPermission(PermissionCodes.FieldsDelete, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<RelationshipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveField(Guid appId, Guid id, Guid fieldId, CancellationToken ct)
    {
        var result = await _removeField.HandleAsync(new RemoveRelationshipFieldCommand(id, fieldId), ct);
        return Ok(new ApiResponse<RelationshipDto>(result));
    }

    /// <summary>Delete a relationship and its reference/lookup/summary fields.</summary>
    [HttpDelete("apps/{appId:guid}/relationships/{id:guid}")]
    [RequireAppPermission(PermissionCodes.FieldsDelete, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid appId, Guid id, [FromQuery] bool force, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteRelationshipCommand(id, force), ct);
        return NoContent();
    }

    /// <summary>List selectable parent records for a Reference field picker.</summary>
    [HttpGet("tables/{tableId:guid}/relationships/{relId:guid}/parent-options")]
    [RequireAppPermission(PermissionCodes.RecordsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<ParentOptionsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ParentOptions(
        Guid tableId, Guid relId, [FromQuery] string? search, [FromQuery] int take, CancellationToken ct)
    {
        var result = await _parentOptions.HandleAsync(relId, search, take, ct);
        return Ok(new ApiResponse<ParentOptionsResponse>(new ParentOptionsResponse(result.Headers, result.Options)));
    }
}
