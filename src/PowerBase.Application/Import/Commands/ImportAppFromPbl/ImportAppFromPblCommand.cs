using PowerBase.Application.Import.Mapping;

namespace PowerBase.Application.Import.Commands.ImportAppFromPbl;

public enum ImportMode
{
    /// <summary>The only mode Phase 1 supports: imports the PBL document as a brand-new app.
    /// Update Existing App and Create Child App Linked to Master arrive in a later phase,
    /// once Master/ownership infrastructure exists.</summary>
    CreateNewApp,

    /// <summary>Applied through the update endpoint (<c>POST /apps/import/update</c>), not the create one.</summary>
    UpdateExistingApp,
}

/// <summary>Raw PBL JSON text (already converted from QBL, in later phases) to import.</summary>
/// <param name="Mapping">Optional; only <c>ExcludedTableRefs</c> is used when creating a new app.</param>
public record ImportAppFromPblCommand(string PblJson, ImportMode Mode = ImportMode.CreateNewApp, ImportMapping? Mapping = null);
