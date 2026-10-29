namespace PowerBase.Application.Records;

/// <summary>
/// The most records <see cref="Common.Interfaces.IRecordRepository.ListAllMatchingAsync"/> reads at once. Sorting or
/// filtering by a calculated (Formula / Lookup / Summary) or encrypted field happens in memory over that read, so on a
/// table with more matching records than this the order, count and calculated totals can only cover the first this-many
/// Capped at 10,000 to prevent GC heap exhaustion and OutOfMemoryException on high-concurrency instances.
/// </summary>
public static class InMemoryReadLimit
{
    public const int Rows = 10_000;
}
