using System.Security.Cryptography;
using System.Text;

namespace PowerBase.Application.Pipelines;

/// <summary>Where Copy Records keeps every rejected record of a run, so the run history can page through all of them
/// (the step output itself only carries a short preview). They live in the step's idempotency receipts, in pages.</summary>
public static class CopyFailedRecordStore
{
    public const int PageSize = 250;
    /// <summary>Rejected records kept per run; the run's error count always has the full total.</summary>
    public const int MaxStored = 100_000;
    /// <summary>Rejected records shown straight in the step output.</summary>
    public const int PreviewCount = 25;

    /// <summary>The receipt namespace of one Copy Records execution (must match the executor's own prefix).</summary>
    public static string Prefix(string executionPath) => executionPath + "/copy";
    public static string PageSuffix(int pageIndex) => $"/failed/{pageIndex}";
    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
