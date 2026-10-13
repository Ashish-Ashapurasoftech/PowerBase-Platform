namespace PowerBase.Application.Pipelines;

/// <summary>One source record Copy Records could not write, and why.</summary>
public sealed record CopyFailedRecord(
    string SourcePublicId,
    long? SourceRecordId,
    string? MergeValue,
    string Reason,
    Dictionary<string, string?>? Values = null);
