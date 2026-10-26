using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Jobs;

namespace PowerBase.Application.Fields;

/// <summary>
/// Chooses the Fid of a new field. Outside an import this is always "the next free number in the table" (unchanged behaviour).
/// During an import a field keeps its Quickbase <c>Id</c> when that number is usable: at least 6 (1–5 are the system fields, the
/// same numbers Quickbase uses) and not taken in the table. A number that cannot be kept falls back to the next free one and is
/// noted so the import report can say so — it never fails the import.
/// </summary>
public static class FieldFidPolicy
{
    public const int FIRST_USER_FID = 6;

    public static async Task<int> ResolveAsync(IAppFieldRepository fieldRepo, long tableId, string label, int? explicitFid, CancellationToken ct, bool useExpected = true)
    {
        var plan = ImportRuntime.Fids;
        int? wanted = explicitFid;
        if (wanted is null && useExpected && plan is not null && plan.TryTake(tableId, label, out var expected))
            wanted = expected;

        if (wanted is { } fid)
        {
            if (fid >= FIRST_USER_FID && await fieldRepo.GetByFidInTableAsync(tableId, fid, ct) is null)
                return fid;
            plan?.NoteFallback($"Field '{label}' could not keep its Quickbase id {fid} (already used or reserved); it was given the next free number.");
        }

        return await fieldRepo.GetNextFidAsync(tableId, ct);
    }
}
