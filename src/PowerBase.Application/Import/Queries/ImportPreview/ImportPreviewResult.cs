namespace PowerBase.Application.Import.Queries.ImportPreview;

public sealed class PblIssueDto
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? ElementRef { get; init; }
}

public sealed class ImportPreviewFieldItem
{
    public string LogicalRef { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TypeCode { get; init; } = string.Empty;

    /// <summary>False when this field's type isn't creatable by this import phase; the field
    /// will be skipped (and reported) rather than imported.</summary>
    public bool IsSupported { get; init; }
}

public sealed class ImportPreviewTableItem
{
    public string LogicalRef { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>Phase 1: always "Local" — mapping to an existing Master App table is not yet
    /// available (see Master App feature, M4). The wizard shows this choice as disabled.</summary>
    public string MappingChoice { get; init; } = "Local";
    public bool MasterMappingAvailable { get; init; }

    public List<ImportPreviewFieldItem> Fields { get; init; } = [];

    /// <summary>Report names detected for this table. Column/sort resolution against the
    /// created fields happens at import time, not in the preview.</summary>
    public List<string> Reports { get; init; } = [];
}

public sealed class ImportPreviewResult
{
    public bool IsValid { get; init; }
    public string AppName { get; init; } = string.Empty;
    public List<ImportPreviewTableItem> Tables { get; init; } = [];
    public List<PblIssueDto> Errors { get; init; } = [];
    public List<PblIssueDto> Warnings { get; init; } = [];
}
