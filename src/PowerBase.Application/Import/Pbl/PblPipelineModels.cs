namespace PowerBase.Application.Import.Pbl;

/// <summary>
/// A Quickbase pipeline converted for import as a PowerFlow. Everything that can only be known once the applications exist —
/// app and table ids, field numbers, the connection — is kept as a reference value (<see cref="PblPipelineAppValue"/>,
/// <see cref="PblPipelineTableValue"/>, <see cref="PblPipelineFieldValue"/>, <see cref="PblPipelineConnectionValue"/>,
/// <see cref="PblPipelineTextValue"/>, <see cref="PblPipelineFieldMapValue"/>) inside <see cref="PblPipelineStep.Config"/> and
/// resolved by <c>PblPipelinesImporter</c>. Steps PowerFlows cannot represent are not here: they are listed in
/// <see cref="Issues"/> instead, never dropped silently.
/// </summary>
public sealed class PblPipeline
{
    public string LogicalRef { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>The source pipeline's owner. PowerFlows makes the importing user the owner, as Quickbase does on create.</summary>
    public string? Owner { get; set; }
    public string? Crontab { get; set; }
    public string? CrontabTimezone { get; set; }

    /// <summary>The application the PowerFlow is created in: the one its trigger (or first step) reads. Null when the file does
    /// not say, and the pipeline is then not created.</summary>
    public string? AppRef { get; set; }

    /// <summary>Every step of the source pipeline, nested ones included.</summary>
    public int SourceStepCount { get; set; }

    /// <summary>Root steps in order. Only steps that are imported.</summary>
    public List<PblPipelineStep> Steps { get; set; } = [];

    public List<PblPipelineIssue> Issues { get; set; } = [];

    /// <summary>Set when the pipeline could not be read at all; it is reported and not created.</summary>
    public bool IsUnreadable { get; set; }

    /// <summary>True when what is imported does not behave like the source: a step was skipped or a value was dropped.</summary>
    public bool ChangesExecution => Issues.Any(i => i.ChangesExecution);

    public int SkippedStepCount => Issues.Count(i => i.IsSkippedStep);
}

/// <summary>One PowerFlows step. <see cref="Type"/>/<see cref="Subtype"/> are the step-picker values the editor and engine use.</summary>
public sealed class PblPipelineStep
{
    public string RefId { get; set; } = string.Empty;

    /// <summary>The Quickbase step <c>Id</c> (<c>aa</c>, <c>ab</c>…). Null for steps Quickbase gives no id (Condition, Loop, Stop).</summary>
    public string? SourceId { get; set; }

    /// <summary>Where the step sits in the source pipeline (<c>3</c>, <c>8.Then.1</c>), for the report.</summary>
    public string Location { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Subtype { get; set; } = string.Empty;

    /// <summary>The step configuration in the shape the PowerFlows editor saves, with reference values still to resolve.</summary>
    public Dictionary<string, object?> Config { get; set; } = new(StringComparer.Ordinal);

    /// <summary>False when the conversion already knows the step is incomplete; it is then saved as a draft, which keeps the
    /// PowerFlow from being turned on until it is fixed.</summary>
    public bool IsComplete { get; set; } = true;

    /// <summary>Loop body, or a Condition's true branch.</summary>
    public List<PblPipelineStep> Children { get; set; } = [];

    /// <summary>A Condition's false branch.</summary>
    public List<PblPipelineStep> ElseChildren { get; set; } = [];

    /// <summary>Handle Errors: what runs when the monitored steps (<see cref="Children"/>) succeed.</summary>
    public List<PblPipelineStep> SuccessChildren { get; set; } = [];

    /// <summary>Handle Errors: what runs when a monitored step fails.</summary>
    public List<PblPipelineStep> ErrorChildren { get; set; } = [];

    public string SourceLabel => SourceId ?? $"(no id) at {Location}";
}

public sealed class PblPipelineIssue
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    /// <summary>The Quickbase step id, or its location when the step has none. Null for pipeline-level notes.</summary>
    public string? StepId { get; init; }
    public string? StepName { get; init; }
    /// <summary>The step is not part of the imported PowerFlow.</summary>
    public bool IsSkippedStep { get; init; }
    /// <summary>The imported PowerFlow does not do what the source did (a step or a value is missing).</summary>
    public bool ChangesExecution { get; init; }
}

/// <summary>The id of an imported application.</summary>
public sealed record PblPipelineAppValue(string AppRef);

/// <summary>The id of an imported table.</summary>
public sealed record PblPipelineTableValue(string AppRef, string TableRef);

/// <summary>An imported field, written as <c>fid_N</c>. <paramref name="SourceId"/> and <paramref name="Label"/> come from the
/// source field; <paramref name="IsBuiltIn"/> marks Record ID#, Date Created and the other fields every table has.
/// <paramref name="Part"/> names one part of an Address field (<c>city</c>…), written as <c>fid_N.city</c>.</summary>
public sealed record PblPipelineFieldValue(string AppRef, string TableRef, string FieldRef, int? SourceId, string Label, bool IsBuiltIn, string? Part = null);

/// <summary>The connection a PowerBase step runs through: the realm the app is imported into.</summary>
public sealed record PblPipelineConnectionValue;

/// <summary>Text made of literal pieces and field references (<c>{{steps.aa.</c> + field + <c>}}</c>).</summary>
public sealed record PblPipelineTextValue(IReadOnlyList<object> Parts);

/// <summary>An object keyed by fields (a bulk upsert row): <c>{ "fid_N": value }</c>.</summary>
public sealed record PblPipelineFieldMapValue(IReadOnlyList<(PblPipelineFieldValue Field, object? Value)> Entries);
