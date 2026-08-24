using System;
using System.Collections.Generic;
using PowerBase.API.Models.Pipelines;

namespace PowerBase.API.Models.Pipelines;

/// <summary>
/// Combined pipeline editor response. Contains pipeline data PLUS all
/// editor metadata resolved server-side. The frontend must not show the
/// editor until EditorTables are populated and ClientResolveRefs are resolved.
/// </summary>
public class PipelineEditorResponse
{
    // ─── Pipeline fields (matches PipelineDetailResponse exactly) ────────────────
    public Guid PublicId { get; set; }
    public Guid AppPublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? VariablesJson { get; set; }
    public bool IsActive { get; set; }
    public string RowVersion { get; set; } = string.Empty;
    public List<PipelineStepResponse> Steps { get; set; } = new();

    // ─── Server-resolved editor metadata ─────────────────────────────────────────
    /// <summary>
    /// Tables and their fields that the backend resolved authoritatively.
    /// One entry per unique (connectionPublicId, tablePublicId) pair.
    /// </summary>
    public List<PipelineEditorTableDto> EditorTables { get; set; } = new();

    /// <summary>
    /// References that the frontend must resolve before marking hydration complete.
    /// These are SavedConnection or System-connection table references only.
    /// </summary>
    public List<PipelineEditorClientRefDto> ClientResolveRefs { get; set; } = new();
}

/// <summary>Table with complete field metadata for editor hydration.</summary>
public class PipelineEditorTableDto
{
    /// <summary>
    /// The connectionPublicId from the step config.
    /// Empty string = current-tenant (no connection).
    /// For CurrentUser cross-tenant steps this is the target tenant's publicId GUID.
    /// </summary>
    public string ConnectionPublicId { get; set; } = string.Empty;

    public Guid AppPublicId { get; set; }
    public Guid TablePublicId { get; set; }
    public string TableName { get; set; } = string.Empty;
    public List<PipelineEditorFieldDto> Fields { get; set; } = new();
}

/// <summary>Field metadata sufficient for FID↔name translation and UI display.</summary>
public class PipelineEditorFieldDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string TypeCode { get; set; } = string.Empty;
    public int? Fid { get; set; }
    public string? Settings { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsRequired { get; set; }
    public bool IsSystem { get; set; }
}

/// <summary>
/// A reference that the frontend must resolve via PipelineTenantMetadataService.
/// Reason clarifies why the backend could not resolve this reference.
/// </summary>
public class PipelineEditorClientRefDto
{
    public string ConnectionPublicId { get; set; } = string.Empty;
    public Guid? AppPublicId { get; set; }
    public Guid? TablePublicId { get; set; }

    /// <summary>
    /// One of: "saved_connection", "system_connection", "table_not_found",
    /// "app_not_found", "access_denied", "tenant_not_found", "connection_unavailable",
    /// "resolution_error"
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
