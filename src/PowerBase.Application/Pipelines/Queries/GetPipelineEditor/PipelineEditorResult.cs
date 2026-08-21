using System;
using System.Collections.Generic;

namespace PowerBase.Application.Pipelines.Queries.GetPipelineEditor;

/// <summary>
/// Full result for the pipeline editor endpoint. Contains pipeline data plus
/// authoritative metadata for all step references that the backend can resolve.
/// </summary>
public class PipelineEditorResult
{
    // ─── Pipeline identity ───────────────────────────────────────────────────────
    public Guid PublicId { get; set; }
    public Guid AppPublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? VariablesJson { get; set; }
    public bool IsActive { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    // ─── Reconstructed step hierarchy (mirrors existing GetPipeline result) ──────
    public List<PipelineEditorStepResult> Steps { get; set; } = new();

    // ─── Fully resolved editor metadata (current-tenant + cross-tenant) ──────────
    /// <summary>
    /// One entry per unique (connection, table) pair that the backend could resolve.
    /// Contains complete field metadata — the frontend does not need to make any
    /// additional GET /tables/{id}/fields call for these entries.
    /// </summary>
    public List<PipelineEditorTableMetadata> EditorTables { get; set; } = new();

    // ─── References the backend cannot resolve (requires client-side resolution) ──
    /// <summary>
    /// SavedConnection and System-connection references that require the frontend
    /// to call PipelineTenantMetadataService.getFields() as part of hydration.
    /// Must be resolved BEFORE the editor is declared ready.
    /// </summary>
    public List<PipelineEditorClientRef> ClientResolveRefs { get; set; } = new();
}

/// <summary>
/// Step result matching GetPipeline's PipelineStepResult; kept in the same
/// namespace so controller mapping is straightforward.
/// </summary>
public class PipelineEditorStepResult
{
    public Guid PublicId { get; set; }
    public string RefId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsValidated { get; set; }
    public DateTime? LastTriggeredOn { get; set; }
    public int DisplayOrder { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Subtype { get; set; } = string.Empty;
    public string? ConfigJson { get; set; }
    public string? ParentBranch { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<PipelineEditorStepResult> Children { get; set; } = new();
    public List<PipelineEditorStepResult> ElseChildren { get; set; } = new();
    public List<PipelineEditorStepResult> SuccessChildren { get; set; } = new();
    public List<PipelineEditorStepResult> ErrorChildren { get; set; } = new();
}

/// <summary>
/// Resolved table with full field metadata for editor hydration.
/// </summary>
public class PipelineEditorTableMetadata
{
    /// <summary>
    /// The connectionPublicId from the step config that led to this table.
    /// Empty string means current-tenant / owner-tenant (no connection).
    /// For CurrentUser cross-tenant steps this is the tenant's publicId.
    /// </summary>
    public string ConnectionPublicId { get; set; } = string.Empty;

    public Guid AppPublicId { get; set; }
    public Guid TablePublicId { get; set; }
    public string TableName { get; set; } = string.Empty;
    public List<PipelineEditorFieldMetadata> Fields { get; set; } = new();
}

/// <summary>
/// Field metadata sufficient for the pipeline editor to:
/// (a) display field name labels, (b) translate FIDs to names on load,
/// (c) translate names back to FIDs on save.
/// </summary>
public class PipelineEditorFieldMetadata
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
/// Classification for why a reference could not be resolved server-side.
/// </summary>
public enum PipelineEditorRefReason
{
    /// <summary>The connectionPublicId is a PipelineConnection (external credentials).</summary>
    SavedConnection,
    /// <summary>The connectionPublicId is one of the three System sentinel GUIDs.</summary>
    SystemConnection,
    /// <summary>The referenced table was not found (may have been deleted).</summary>
    TableNotFound,
    /// <summary>The referenced app was not found.</summary>
    AppNotFound,
    /// <summary>The current user does not have access to the target tenant/app/table.</summary>
    AccessDenied,
    /// <summary>The tenant identified by connectionPublicId could not be resolved.</summary>
    TenantNotFound,
    /// <summary>An unexpected infrastructure error occurred resolving this reference.</summary>
    ResolutionError
}

/// <summary>
/// A reference that must be resolved by the frontend before the editor is ready.
/// </summary>
public class PipelineEditorClientRef
{
    /// <summary>The raw connectionPublicId string from the step config.</summary>
    public string ConnectionPublicId { get; set; } = string.Empty;

    /// <summary>AppPublicId if extractable from config; null otherwise.</summary>
    public Guid? AppPublicId { get; set; }

    /// <summary>TablePublicId if extractable from config; null otherwise.</summary>
    public Guid? TablePublicId { get; set; }

    public PipelineEditorRefReason Reason { get; set; }
}
