using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Mapping;

namespace PowerBase.Application.Import.Commands.UpdateAppFromPbl;

/// <param name="Content">The uploaded QBL (YAML) or PBL (JSON) text.</param>
/// <param name="TargetAppPublicId">The existing app to update.</param>
/// <param name="Mapping">The user's per-element decisions.</param>
public record UpdateAppFromPblCommand(string Content, Guid TargetAppPublicId, ImportMapping Mapping);

public static class ImportOutcome
{
    public const string Added = "Added";
    public const string Overwritten = "Overwritten";
    /// <summary>Left as-is: the user chose Skip, or it could not be applied (see Reason).</summary>
    public const string Skipped = "Skipped";
    public const string Unchanged = "Unchanged";
    public const string Failed = "Failed";
}

public sealed class ImportElementOutcome
{
    public string ElementType { get; init; } = string.Empty;
    public string SourceLogicalRef { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? TableName { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? Reason { get; init; }
}

public sealed class ImportUpdateReport
{
    public Guid AppPublicId { get; init; }
    public string AppName { get; init; } = string.Empty;
    public List<ImportElementOutcome> Elements { get; init; } = [];
    public int Added { get; init; }
    public int Overwritten { get; init; }
    public int SkippedCount { get; init; }
    public int Unchanged { get; init; }
    public int Failed { get; init; }

    /// <summary>Elements in the app but not in the file — always preserved.</summary>
    public int PreservedOnlyInApp { get; init; }

    /// <summary>Stage-level errors. When non-empty some elements may not have been applied; re-running
    /// the same import is safe — elements already added now match and are not created twice.</summary>
    public List<string> Failures { get; init; } = [];

    public List<ImportSkippedItem> Skipped { get; init; } = [];
    public List<FormulaTranslationReportItem> FormulaTranslations { get; init; } = [];
}
