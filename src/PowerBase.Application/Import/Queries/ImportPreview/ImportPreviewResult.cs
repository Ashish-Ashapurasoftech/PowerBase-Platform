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

    /// <summary>True for formula fields, whose expression can only be compiled once the table's
    /// fields actually exist and have Fids — which happens during import, not preview. Preview
    /// can confirm the type is importable but not that the formula will translate, so these are
    /// counted separately rather than promised outright.</summary>
    public bool IsPendingValidation { get; init; }
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

public sealed class ImportPreviewRelationshipItem
{
    public string LogicalRef { get; init; } = string.Empty;
    public string ParentTableRef { get; init; } = string.Empty;
    public string ChildTableRef { get; init; } = string.Empty;
    public string ReferenceFieldName { get; init; } = string.Empty;
    public int LookupCount { get; init; }
    public int SummaryCount { get; init; }
}

public sealed class ImportPreviewFormItem
{
    public string LogicalRef { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TableRef { get; init; } = string.Empty;
    public int SectionCount { get; init; }
    public int RuleCount { get; init; }
}

public sealed class ImportPreviewRoleItem
{
    public string LogicalRef { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int TablePermissionCount { get; init; }
}

public sealed class ImportPreviewResult
{
    public bool IsValid { get; init; }
    public string AppName { get; init; } = string.Empty;
    public List<ImportPreviewTableItem> Tables { get; init; } = [];
    public List<ImportPreviewRelationshipItem> Relationships { get; init; } = [];
    public List<ImportPreviewFormItem> Forms { get; init; } = [];
    public List<ImportPreviewRoleItem> Roles { get; init; } = [];
    public List<PblIssueDto> Errors { get; init; } = [];
    public List<PblIssueDto> Warnings { get; init; } = [];
}
