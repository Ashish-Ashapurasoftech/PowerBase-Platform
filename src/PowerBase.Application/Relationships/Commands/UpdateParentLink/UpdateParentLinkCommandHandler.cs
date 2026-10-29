using System.Text.Json.Nodes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Commands.UpdateParentLink;

/// <summary>Sets <c>displayAsLinkToParent</c> in the settings of a relationship's Reference field or one of its Lookup
/// fields: with it on, the field's value opens the parent record on view forms and in reports (Quickbase's
/// DisplayAsLinkToParent, off by default). Only that one property is written — every other setting is kept as stored.</summary>
public class UpdateParentLinkCommandHandler
{
    private const string PropertyName = "displayAsLinkToParent";

    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly RelationshipQueriesHandler _queries;
    private readonly IAuditRepository _auditRepo;

    public UpdateParentLinkCommandHandler(
        IRelationshipRepository relRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        RelationshipQueriesHandler queries,
        IAuditRepository auditRepo)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _queries = queries;
        _auditRepo = auditRepo;
    }

    public async Task<RelationshipDto> HandleAsync(UpdateParentLinkCommand command, CancellationToken ct = default)
    {
        var rel = await _relRepo.GetByPublicIdAsync(command.RelationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", command.RelationshipPublicId);
        var field = await _fieldRepo.GetByPublicIdAsync(command.FieldPublicId, ct)
            ?? throw new NotFoundException("Field", command.FieldPublicId);

        bool current;
        if (field.Id == rel.ReferenceFieldId)
            current = FormulaTypeMap.ParseReferenceSettings(field.Settings)?.DisplayAsLinkToParent ?? false;
        else if (field.TypeCode == "Lookup" && FormulaTypeMap.ParseLookupSettings(field.Settings) is { } lookup
                 && lookup.RelationshipId == rel.Id)
            current = lookup.DisplayAsLinkToParent;
        else
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["fieldId"] = ["Only the relationship's reference field and its lookup fields can link to the parent record."],
            });

        if (current != command.DisplayAsLinkToParent)
        {
            await _fieldRepo.UpdateSettingsAsync(field.Id, WithFlag(field.Settings, command.DisplayAsLinkToParent), ct);

            var child = await _tableRepo.GetByIdAsync(rel.ChildTableId, ct);
            var label = field.Label ?? field.Name;
            await _auditRepo.LogActivityAsync(
                AuditActions.SchemaChanged, AuditEntityTypes.AppField, field.PublicId.ToString(),
                command.DisplayAsLinkToParent
                    ? $"Field '{label}' on {child.Name} now links to the parent record"
                    : $"Field '{label}' on {child.Name} no longer links to the parent record",
                appId: child.AppId, ct: ct);
        }

        return await _queries.GetAsync(rel.PublicId, ct);
    }

    /// <summary>The settings JSON with only the link flag changed (any differently-cased copy of it replaced).</summary>
    public static string WithFlag(string? settingsJson, bool value)
    {
        JsonObject settings;
        try
        {
            settings = string.IsNullOrWhiteSpace(settingsJson) ? new JsonObject() : JsonNode.Parse(settingsJson) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            settings = new JsonObject();
        }
        foreach (var key in settings.Select(p => p.Key).Where(k => string.Equals(k, PropertyName, StringComparison.OrdinalIgnoreCase)).ToList())
            settings.Remove(key);
        settings[PropertyName] = value;
        return settings.ToJsonString();
    }
}
