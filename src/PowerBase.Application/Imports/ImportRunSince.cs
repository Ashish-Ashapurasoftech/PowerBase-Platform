using System.Security.Cryptography;
using System.Text;

namespace PowerBase.Application.Imports;

/// <summary>When "only records changed since the last run" may skip records: only if the import still does what it did then. The last run's own copy of the
/// definition is compared with the current one on everything that decides what a record becomes (mapping, rules, filters, type, key, tables); who is told,
/// the schedule and the name do not count. An import edited since has to read everything once, because records nothing touched have never been through
/// its new mapping.</summary>
public static class ImportRunSince
{
    /// <summary>How far before the last run's start the records are read from: a record saved while that run was starting may not have been seen by it.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    public static string Fingerprint(ImportDefinitionConfig config)
    {
        // A copy without what does not change the result: the name, the people told, the schedule, the option itself and the run's own moment.
        var same = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(config))!;
        same.Name = "";
        same.NotifyEmails = [];
        same.Schedule = null;
        same.OnlyChangedSinceLastRun = false;
        same.ChangedSinceUtc = null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ImportJson.Serialize(same))));
    }

    /// <summary>The moment records must have been created or changed after to be read in this run, or null to read every record: the import asks for it, a run
    /// has finished before, and the import has not changed since.</summary>
    public static DateTime? Since(ImportDefinitionConfig config, (DateTime StartedOn, string SnapshotJson)? last)
    {
        if (!config.OnlyChangedSinceLastRun || last is null) return null;
        ImportDefinitionConfig? before;
        try { before = ImportJson.Deserialize<ImportRunSnapshot>(last.Value.SnapshotJson)?.Config; }
        catch (System.Text.Json.JsonException) { return null; } // a copy that cannot be read says nothing about what the last run did: read everything
        if (before is null || Fingerprint(before) != Fingerprint(config)) return null;
        return DateTime.SpecifyKind(last.Value.StartedOn, DateTimeKind.Utc) - Margin;
    }
}
