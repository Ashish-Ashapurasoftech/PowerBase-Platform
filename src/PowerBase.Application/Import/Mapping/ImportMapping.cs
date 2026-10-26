using System.Text.Json;
using System.Text.Json.Serialization;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Mapping;

public enum ImportElementAction
{
    /// <summary>Create an element that does not yet exist in the target app.</summary>
    Add,
    /// <summary>Apply the file's values onto the matched existing element (user-approved).</summary>
    Overwrite,
    /// <summary>Leave the existing element untouched / do not create the new one.</summary>
    Skip,
}

/// <summary>One per-element decision. Elements are identified by their source logical ref — never
/// a DBID. <see cref="TargetElementPublicId"/> optionally overrides which existing element the
/// logical ref is matched to (resolving a rename the automatic match could not).</summary>
public sealed class ImportMappingElement
{
    public string ElementType { get; set; } = string.Empty;
    public string SourceLogicalRef { get; set; } = string.Empty;
    public ImportElementAction Action { get; set; }
    public Guid? TargetElementPublicId { get; set; }
}

/// <summary>New-app import of a file with several applications: what to do with one of them. Table refs are
/// only unique inside an application, so exclusions are scoped here.</summary>
public sealed class ImportMappingApp
{
    public string AppRef { get; set; } = string.Empty;
    public bool Include { get; set; } = true;
    public string? NameOverride { get; set; }
    public List<string> ExcludedTableRefs { get; set; } = [];
}

/// <summary>The user's decisions for an import. Built by the wizard; also accepted as a JSON file
/// by the API (developer use).</summary>
public sealed class ImportMapping
{
    public List<ImportMappingElement> Elements { get; set; } = [];

    /// <summary>Table logical refs the user chose not to import (applies to Create and Update). For a
    /// multi-application file use <see cref="Apps"/>; this list then applies to the only application.</summary>
    public List<string> ExcludedTableRefs { get; set; } = [];

    /// <summary>Per-application choices (New App only). An application not listed is imported as is.</summary>
    public List<ImportMappingApp> Apps { get; set; } = [];

    public ImportMappingApp? ForApp(string appRef) => Apps.FirstOrDefault(a => string.Equals(a.AppRef, appRef, StringComparison.Ordinal));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ImportMapping Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ImportMapping();
        try
        {
            return JsonSerializer.Deserialize<ImportMapping>(json, JsonOptions) ?? new ImportMapping();
        }
        catch (JsonException ex)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["Mapping"] = [$"Could not parse mapping: {ex.Message}"] });
        }
    }
}
