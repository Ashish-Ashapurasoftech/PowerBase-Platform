using System.Text.Json.Nodes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

/// <summary>Gives a Reference field the place the platform keeps a value that matched no parent record: the hidden "(original)" text field named in its
/// settings (<c>ReferenceSettings.OriginalFid</c>), whose text the grid, reports and exports show where the reference is blank. A Reference converted from
/// a text field has one already; one created straight into a relationship does not, so the first import that meets a value with no parent record makes it
/// (the same hidden field the conversion makes) rather than lose the text. Only for a person who may create fields in the table.</summary>
public interface IReferenceOriginalFields
{
    /// <returns>The hidden field the reference keeps unmatched text in, or null when there is none and it cannot be made.</returns>
    Task<AppField?> EnsureAsync(AppTable child, AppField reference, CancellationToken ct);
}

public sealed class ReferenceOriginalFields(
    RelationshipFieldFactory factory, IAppFieldRepository fields, IAppRepository apps, IAppAccessService access) : IReferenceOriginalFields
{
    private readonly Dictionary<long, AppField?> _done = new();

    public async Task<AppField?> EnsureAsync(AppTable child, AppField reference, CancellationToken ct)
    {
        if (_done.TryGetValue(reference.Id, out var known)) return known;

        var tableFields = await fields.ListByTableAsync(child.Id, ct);
        var current = tableFields.FirstOrDefault(f => f.Id == reference.Id) ?? reference;
        if (FormulaTypeMap.ParseReferenceSettings(current.Settings)?.OriginalFid is int original
            && tableFields.FirstOrDefault(f => f.Fid == original && !f.IsDeleted) is { } existing)
            return _done[reference.Id] = existing;

        try { await access.RequirePermissionByTablePublicIdAsync(child.PublicId, PermissionCodes.FieldsCreate, ct); }
        catch (UnauthorizedActionException) { return _done[reference.Id] = null; } // an import does not change a table's fields for someone who may not

        var app = await apps.GetByIdAsync(child.AppId, ct);
        var label = $"{(string.IsNullOrWhiteSpace(current.Label) ? current.Name : current.Label)} (original)";
        var created = await factory.CreateAsync(child, nameof(Domain.Enums.FieldTypeCode.Text), label, false, new Dictionary<string, bool>(), ct,
            isEncrypted: app?.IsEncrypted == true, hidden: true);
        await fields.UpdateSettingsAsync(current.Id, WithOriginal(current.Settings, created.Fid!.Value), ct);
        return _done[reference.Id] = created;
    }

    /// <summary>The reference's settings with <c>originalFid</c> set, the rest left as they were.</summary>
    public static string WithOriginal(string? settings, int originalFid)
    {
        var node = (string.IsNullOrWhiteSpace(settings) ? null : JsonNode.Parse(settings) as JsonObject) ?? new JsonObject();
        foreach (var key in node.Select(p => p.Key).Where(k => string.Equals(k, "originalFid", StringComparison.OrdinalIgnoreCase)).ToList()) node.Remove(key);
        node["originalFid"] = originalFid;
        return node.ToJsonString();
    }
}
