namespace PowerBase.Domain.Constants;

/// <summary>Size limits for writes that add or change many records in one request.</summary>
public static class RecordLimits
{
    /// <summary>Most records one bulk-create request (Grid Edit's new rows) may add. Bigger loads belong in Import.</summary>
    public const int MaxBulkCreate = 500;
}
