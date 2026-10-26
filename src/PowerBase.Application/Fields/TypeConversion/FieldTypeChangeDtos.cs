using PowerBase.Application.Fields.Queries.GetFieldUsage;

namespace PowerBase.Application.Fields.TypeConversion;

public sealed record FieldTypeChangeTargetDto(
    string Code, string DisplayName, string Category, string? Icon, string Kind, string DataLoss);

public sealed record FieldTypeChangeTargetsDto(
    string CurrentTypeCode,
    string CurrentTypeName,
    bool CanChangeType,
    string? Reason,
    IReadOnlyList<FieldTypeChangeTargetDto> Targets);

public sealed record FieldTypeChangeSampleDto(long RowId, string? Before, string? After);

public sealed record DependentFieldDto(string TableName, string FieldLabel);

public sealed record BrokenFormulaDto(string TableName, string FieldLabel, string Message);

public sealed class FieldTypeChangePreviewDto
{
    public required string SourceTypeCode { get; init; }
    public required string TargetTypeCode { get; init; }
    public string? Kind { get; init; }
    public string? DataLoss { get; init; }
    public bool CanChange { get; init; }
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int TotalRecords { get; init; }
    public int NonEmpty { get; init; }
    public int WillBecomeBlank { get; init; }
    public IReadOnlyList<FieldTypeChangeSampleDto> Samples { get; init; } = [];

    /// <summary>The field is Unique and converted values repeat: Unique will be turned off.</summary>
    public int DuplicateValues { get; init; }
    public int? ChoiceCount { get; init; }

    public IReadOnlyList<BrokenFormulaDto> BrokenFormulas { get; init; } = [];
    public IReadOnlyList<DependentFieldDto> LookupsUpdated { get; init; } = [];
    public IReadOnlyList<DependentFieldDto> SummariesUpdated { get; init; } = [];
    public FieldUsageDto? Usage { get; init; }
}

public sealed record ChangeFieldTypeResultDto(
    string OldTypeCode,
    string NewTypeCode,
    int RecordsScanned,
    int ValuesKept,
    int ValuesBlanked,
    IReadOnlyList<string> Warnings);
