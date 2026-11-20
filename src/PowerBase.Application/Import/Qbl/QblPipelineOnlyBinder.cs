using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PowerBase.Application.Import.Qbl;

/// <summary>A field of an existing PowerBase table, as the pipeline-only import sees it.</summary>
/// <remarks><see cref="ResultTypeCode"/>: what a Lookup / Summary field holds (its settings' resultTypeCode), so filters on it get
/// the operators of that type.</remarks>
public sealed record PipelineOnlyField(int Fid, string Name, string Label, string TypeCode, bool IsSystem, string? ResultTypeCode = null);

/// <summary>A table of an existing PowerBase app. <see cref="KeyFid"/> is its Set-Key field (null = Record ID#).</summary>
public sealed record PipelineOnlyTable(long Id, string Name, int? KeyFid, IReadOnlyList<PipelineOnlyField> Fields);

/// <summary>An existing PowerBase app the pipelines of a pipeline-only QBL file may belong to.</summary>
public sealed record PipelineOnlyApp(Guid PublicId, long Id, string Name, IReadOnlyList<PipelineOnlyTable> Tables);

/// <summary>Which existing app/table a Quickbase app or table id of the file was matched to.</summary>
public sealed record PipelineOnlyTableMatch(string? QbAppId, string QbTableId, Guid AppPublicId, string AppName, long TableId, string TableName);

/// <summary>The result of binding a pipeline-only file: the file rewritten so its pipelines point at the matched apps' tables and
/// fields (<see cref="Content"/>, a normal QBL file with one <c>QB::Application</c> per matched app), and what was matched.</summary>
public sealed class PipelineOnlyBinding
{
    public string Content { get; init; } = string.Empty;
    /// <summary>App ref used in <see cref="Content"/> → the existing app.</summary>
    public List<(string AppRef, PipelineOnlyApp App)> Apps { get; init; } = [];
    public List<PipelineOnlyTableMatch> Matches { get; init; } = [];
    /// <summary>Quickbase table ids of the file no existing table matched; their steps import without a table.</summary>
    public List<string> UnmatchedTables { get; init; } = [];
    /// <summary>Quickbase app ids of the file no existing app clearly matched; their pipelines are not imported.</summary>
    public List<string> UnmatchedApps { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

/// <summary>
/// A Quickbase export of pipelines only (no <c>QB::Application</c>) names apps, tables and fields by their Quickbase ids
/// (<c>App: bt2kqxkqk</c>, <c>Table: bwcrtu9k5</c>, <c>Fields: [7, 20]</c>) instead of <c>!Ref</c>s into the file. Quickbase
/// application exports keep field ids (an imported field's Fid is its Quickbase id) but not table ids, so each Quickbase table
/// is matched to an existing PowerBase table by the field ids its steps list and the field keys its steps use
/// (<c>Employee_Name</c>, <c>{{aa.t_rating}}</c>, filters). The file is then rewritten as if it had been exported with those apps,
/// so the normal pipeline converter and importer handle it — nothing in the apps is created or changed.
/// </summary>
public static class QblPipelineOnlyBinder
{
    private static readonly Regex ApplicationLine = new(@"^\s*Type:\s*QB::Application\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PipelineLine = new(@"^\s*Type:\s*QB::Pipeline\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Token = new(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\.([A-Za-z0-9_]+)", RegexOptions.Compiled);

    private static readonly TagName RefTag = new("!Ref");

    /// <summary>Keys every table has; they say nothing about which table a step uses.</summary>
    private static readonly HashSet<string> BuiltInKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "record_id", "created_at", "date_created", "updated_at", "date_modified", "record_owner", "last_modified_by",
        "name_of_record", "count", "records",
    };

    private static readonly (string Property, string Type, string Label)[] AddressParts =
    [
        ("Street1", "QB::Field::AddressStreet1", "Street 1"),
        ("Street2", "QB::Field::AddressStreet2", "Street 2"),
        ("City", "QB::Field::AddressCity", "City"),
        ("State", "QB::Field::AddressState", "State/Region"),
        ("PostalCode", "QB::Field::AddressPostalCode", "Postal Code"),
        ("Country", "QB::Field::AddressCountry", "Country"),
    ];

    /// <summary>A QBL file with pipelines and no application (cheap text check, no parse).</summary>
    public static bool IsPipelinesOnly(string content) =>
        DocumentFormatDetector.Detect(content) == ImportDocumentFormat.Qbl
        && PipelineLine.IsMatch(content) && !ApplicationLine.IsMatch(content);

    // ------------------------------------------------------------------------------------------------------------------------

    /// <summary>What the file says about one Quickbase table: its app (when a step names it), the field ids listed with it and
    /// the field keys steps read or write on it.</summary>
    internal sealed class TableUse(string table)
    {
        public string Table { get; } = table;
        public string? App { get; set; }
        public HashSet<int> Fids { get; } = [];
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StepInfo
    {
        public string? Id;
        public string? Table;
        public string? App;
        public string? TargetId;
        public readonly List<string> Keys = [];
        /// <summary>Every table data source of the step (table, source_table…) with the field ids listed for it.</summary>
        public readonly List<(string Table, string? App, List<int> Fids)> Tables = [];
    }

    public static PipelineOnlyBinding Bind(string content, IReadOnlyList<PipelineOnlyApp> candidates)
    {
        var previous = _commonKeys;
        var previousMemo = _evidenceMemo;
        _evidenceMemo = null;
        try { return BindCore(content, candidates); }
        finally { _commonKeys = previous; _evidenceMemo = previousMemo; }
    }

    /// <summary>Evidence(table, use) of the current Bind: every use is collected before matching starts, so a pair's fit never
    /// changes while apps, tables and rivals are compared again and again.</summary>
    [ThreadStatic] private static Dictionary<(PipelineOnlyTable, TableUse), Fit>? _evidenceMemo;
    /// <summary>Diagnostics only (tests): receives one line per Quickbase app / app pair considered.</summary>
    [ThreadStatic] public static Action<string>? Trace;

    private sealed class PairComparer : IEqualityComparer<(PipelineOnlyTable, TableUse)>
    {
        public static readonly PairComparer Instance = new();
        public bool Equals((PipelineOnlyTable, TableUse) a, (PipelineOnlyTable, TableUse) b) =>
            ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
        public int GetHashCode((PipelineOnlyTable, TableUse) p) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item1), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item2));
    }

    /// <summary>Field keys that many Quickbase tables of the file use (email, name, status…): finding one says little about
    /// which table a step uses, so it weighs like a field id. Worked out from the file alone, so the result never depends on how
    /// many apps the user has. Set for the duration of one <see cref="Bind"/>.</summary>
    [ThreadStatic] private static CommonKeys? _commonKeys;

    private sealed class CommonKeys(IEnumerable<TableUse> uses)
    {
        private readonly HashSet<string> _common = CommonOf(uses.ToList());

        private static HashSet<string> CommonOf(List<TableUse> uses)
        {
            var threshold = Math.Max(3, uses.Count / 20);
            return uses.SelectMany(u => u.Keys.Select(k => (Key: QblPipelineConverter.NormalizeKey(k).Replace("_", "", StringComparison.Ordinal), u.Table)))
                .Distinct().GroupBy(x => x.Key).Where(g => g.Count() >= threshold).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>The key is used on at least 1 in 20 of the file's Quickbase tables (at least 3).</summary>
        public bool IsCommon(string key) => _common.Contains(QblPipelineConverter.NormalizeKey(key).Replace("_", "", StringComparison.Ordinal));
    }

    private static PipelineOnlyBinding BindCore(string content, IReadOnlyList<PipelineOnlyApp> candidates)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(content))
            stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
            || Child(root, "Resources") is not YamlMappingNode resources)
            return new PipelineOnlyBinding { Content = content };

        // 1. What each Quickbase table is used for, and per pipeline: its tables and its name / tags (they often name the app).
        var parameters = Child(root, "ParameterDefinitions") as YamlMappingNode;
        var uses = new Dictionary<string, TableUse>(StringComparer.Ordinal);
        var pipelineFacts = new List<(HashSet<string> Tables, List<string> Hints)>();
        foreach (var (_, node) in resources.Children)
            if (node is YamlMappingNode resource && Scalar(resource, "Type") == "QB::Pipeline")
                pipelineFacts.Add((CollectPipeline(resource, uses), PipelineHints(resource, parameters)));

        _commonKeys = new CommonKeys(uses.Values);
        _evidenceMemo = new Dictionary<(PipelineOnlyTable, TableUse), Fit>(PairComparer.Instance);

        // A Quickbase page/API link (.../nav/app/{app}/table/{table}/...) also says which app a table belongs to.
        foreach (var scalar in AllScalars(root))
            foreach (Match link in AppTableLink.Matches(scalar))
                if (uses.TryGetValue(link.Groups[2].Value, out var linked)) linked.App ??= link.Groups[1].Value;

        var binding = new PipelineOnlyBinding();
        var tableOf = new Dictionary<string, (PipelineOnlyApp App, PipelineOnlyTable Table)>(StringComparer.Ordinal);
        var chosenApps = new Dictionary<string, PipelineOnlyApp>(StringComparer.Ordinal);

        // 2. Apps. An existing app is the copy of exactly ONE Quickbase app. Quickbase realms hold clones of an app (CARES / DSS /
        // DSP / Voyage: same tables, same field ids), so every Quickbase app is scored against every app and the pairs are taken
        // best first, one-to-one: the share of its evidence that is found (field labels differ between clones), then how many of
        // its pipelines carry the app's name in their name or tags, then the amount of evidence. A tie that nothing breaks
        // matches nothing: a pipeline is never put into an app it may not belong to.
        var groups = uses.Values.Where(u => u.App is not null).GroupBy(u => u.App!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var pairs = new List<(string QbApp, PipelineOnlyApp App, Fit Fit, double Ratio, int Hint, Dictionary<string, PipelineOnlyTable> Tables)>();
        foreach (var (qbApp, group) in groups)
        {
            var hintPipelines = pipelineFacts.Where(p => group.Any(u => p.Tables.Contains(u.Table))).Select(p => p.Hints).ToList();
            foreach (var app in candidates)
            {
                var (fit, assigned) = AssignTables(app, group);
                var idsOnly = fit.Miss == 0 && fit.Hit >= 2 && group.All(u => u.Keys.All(BuiltInKeys.Contains));
                if (assigned.Count == 0 || !(fit.IsStrong || fit.Score >= 10 && fit.KeyHits >= 3 || idsOnly))
                {
                    if (Trace is not null && assigned.Count > 0) Trace($"WEAK {qbApp}	{app.Name}	hit={fit.Hit}	miss={fit.Miss}	key={fit.KeyHits}	hint={hintPipelines.Count(h => h.Any(x => NamesApp(x, app.Name)))}");
                    continue;
                }
                var hint = hintPipelines.Count(h => h.Any(x => NamesApp(x, app.Name)));
                Trace?.Invoke($"PAIR {qbApp}	{app.Name}	hit={fit.Hit}	miss={fit.Miss}	key={fit.KeyHits}	score={fit.Score}	hint={hint}	tables={assigned.Count}/{group.Count}	pipes={hintPipelines.Count}");
                var ratio = Math.Round((double)fit.Hit / Math.Max(1, fit.Hit + fit.Miss), 3);
                // Field ids and common labels (Name, Email, Status…) are shared by unrelated apps: without the app's name in any of
                // the Quickbase app's pipeline names / tags, only near-complete evidence makes it that app.
                var specificKeys = group.SelectMany(u => u.Keys.Where(k => !BuiltInKeys.Contains(k))).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                // In a file of many Quickbase apps (clones among them) a few matching ids say little: then at least 20 found.
                var minHits = groups.Count >= 3 ? 20 : 0;
                if (hint == 0 && !(ratio >= 0.85 && fit.KeyHits >= Math.Max(1, Math.Min(5, specificKeys)) && fit.Hit >= minHits) && !(idsOnly && groups.Count < 3)) continue;
                pairs.Add((qbApp, app, fit, Math.Round((double)fit.Hit / Math.Max(1, fit.Hit + fit.Miss), 3), hint, assigned));
            }
        }
        // Net evidence plus the app's name in the pipelines' names/tags (10 per pipeline).
        static int Rank((string QbApp, PipelineOnlyApp App, Fit Fit, double Ratio, int Hint, Dictionary<string, PipelineOnlyTable> Tables) p) => p.Fit.Score + 10 * p.Hint;
        var ranked = pairs.OrderByDescending(Rank).ThenByDescending(p => p.Ratio).ThenByDescending(p => p.App.Id).ToList();
        var takenApps = new HashSet<long>();
        var blockedQb = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in ranked)
        {
            if (chosenApps.ContainsKey(pair.QbApp) || blockedQb.Contains(pair.QbApp) || takenApps.Contains(pair.App.Id)) continue;
            // A Quickbase app whose best fit is an app another Quickbase app already took is a clone of that one (CARES / DSS /
            // DSP / Voyage): it takes a different app only when its pipelines name that app.
            if (pair.Hint == 0 && ranked.Any(p => p.QbApp == pair.QbApp && p.App.Id != pair.App.Id && takenApps.Contains(p.App.Id)
                    && (Rank(p) > Rank(pair) || (Rank(p) == Rank(pair) && p.Ratio >= pair.Ratio))))
                continue;
            // Two different apps (not copies of each other) fit equally: nothing tells which one it is.
            if (ranked.Any(p => p.QbApp == pair.QbApp && p.App.Id != pair.App.Id && !takenApps.Contains(p.App.Id)
                    && Rank(p) == Rank(pair) && p.Ratio == pair.Ratio && !IsCopyOf(p.App, pair.App)))
            {
                binding.Notes.Add($"Quickbase app {pair.QbApp} fits several apps equally; none was used.");
                blockedQb.Add(pair.QbApp);
                continue;
            }
            var rival = ranked.FirstOrDefault(p => p.QbApp != pair.QbApp && p.App.Id == pair.App.Id
                && !chosenApps.ContainsKey(p.QbApp) && !blockedQb.Contains(p.QbApp)
                && Rank(p) == Rank(pair) && p.Ratio == pair.Ratio);
            if (rival.QbApp is not null)
            {
                binding.Notes.Add($"Quickbase apps {pair.QbApp} and {rival.QbApp} fit '{pair.App.Name}' equally (clones); neither was used.");
                takenApps.Add(pair.App.Id);
                blockedQb.Add(pair.QbApp);
                blockedQb.Add(rival.QbApp);
                continue;
            }
            chosenApps[pair.QbApp] = pair.App;
            takenApps.Add(pair.App.Id);
            // Two existing apps that are copies of each other (same tables and fields) fit equally: the newest is used.
            if (ranked.FirstOrDefault(p => p.QbApp == pair.QbApp && p.App.Id != pair.App.Id && Rank(p) == Rank(pair) && p.Ratio == pair.Ratio) is { QbApp: not null } copy)
                binding.Notes.Add($"Quickbase app '{pair.QbApp}' matches several apps equally ({pair.App.Name}, {copy.App.Name}); the newest, '{pair.App.Name}', was used.");
            // An existing app that is a copy of the chosen one (same tables, same fields) is that same Quickbase app imported again:
            // no other Quickbase app (a clone) may take it.
            foreach (var other in candidates.Where(c => c.Id != pair.App.Id && IsCopyOf(c, pair.App)))
                takenApps.Add(other.Id);
            binding.Notes.Add($"Quickbase app {pair.QbApp} -> '{pair.App.Name}' (evidence found {pair.Ratio:P0}, hit {pair.Fit.Hit} / miss {pair.Fit.Miss}, {pair.Hint} pipeline names/tags name the app).");
            foreach (var (qbTable, table) in pair.Tables)
            {
                tableOf[qbTable] = (pair.App, table);
                binding.Matches.Add(new PipelineOnlyTableMatch(pair.QbApp, qbTable, pair.App.PublicId, pair.App.Name, table.Id, table.Name));
            }
        }
        foreach (var qbApp in groups.Keys.Where(a => !chosenApps.ContainsKey(a)))
            binding.UnmatchedApps.Add(qbApp);

        // 3. Tables no step names an app for (Create Record, Copy Records' source, Quickbase API calls): only a table of a matched
        // app that no other Quickbase table took, with a strong fit no other table shares, and with support that it is that app's
        // table: its id starts like the Quickbase app's id (Quickbase numbers an app's tables after it), or one of its pipelines
        // also uses a table of that app.
        var claimed = tableOf.Values.Select(v => v.Table.Id).ToHashSet();
        var appless = new List<(TableUse Use, PipelineOnlyApp App, PipelineOnlyTable Table, Fit Fit)>();
        var globalPicks = new Dictionary<string, long>(StringComparer.Ordinal);
        // Richest evidence first, so clear matches are known when a clone tie is broken by the pipeline's other tables.
        foreach (var use in uses.Values.OrderByDescending(u => u.Fids.Count + u.Keys.Count).ThenBy(u => u.Table, StringComparer.Ordinal))
        {
            if (use.App is not null)
            {
                if (!tableOf.ContainsKey(use.Table)) binding.UnmatchedTables.Add(use.Table);
                continue;
            }
            var near = pipelineFacts.Where(p => p.Tables.Contains(use.Table)).SelectMany(p => p.Tables)
                .Where(tableOf.ContainsKey).Select(t => tableOf[t].App.Id).ToHashSet();
            bool Fits(Fit fit) => fit.IsStrong || (fit.Miss == 0 && fit.Hit >= 2 && use.Fids.Count(x => x > 5) >= 2 && use.Keys.All(BuiltInKeys.Contains));
            var options = chosenApps
                .Where(kv => near.Contains(kv.Value.Id) || CommonPrefix(kv.Key, use.Table) >= 5)
                .SelectMany(kv => kv.Value.Tables.Where(t => !claimed.Contains(t.Id)).Select(t => (App: kv.Value, Table: t, Fit: Evidence(t, use))))
                .Where(x => Fits(x.Fit))
                .GroupBy(x => x.Table.Id).Select(g => g.First())
                .OrderByDescending(x => x.Fit.Score).ThenByDescending(x => x.App.Id).ToList();
            // Not in a matched Quickbase app's app: an app no Quickbase app of the file was matched to (it may be this table's own
            // app, e.g. a file whose steps never name their app).
            // Only for a file whose steps never name their Quickbase app: otherwise an app-less table belongs to one of the file's
            // Quickbase apps, and one that was not matched must not land in an unrelated app that happens to have similar fields.
            if (options.Count == 0 && groups.Count == 0)
                options = candidates.Where(a => !takenApps.Contains(a.Id))
                    .SelectMany(a => a.Tables.Where(t => !claimed.Contains(t.Id)).Select(t => (App: a, Table: t, Fit: Evidence(t, use))))
                    .Where(x => Fits(x.Fit))
                    .OrderByDescending(x => x.Fit.Score).ThenByDescending(x => x.App.Id).ToList();
            // 2026-11-19: most steps name only their table, never its Quickbase app. With many apps imported (every app of the
            // realm), such a table is matched among ALL apps — but only with the app named by the pipelines that use it (their
            // name / tags, "... (Destiny Financial)"), or, with no name at all, when one table of all apps clearly fits best
            // (clones fit equally and then match nothing).
            if (options.Count == 0)
            {
                var mates = pipelineFacts.Where(p => p.Tables.Contains(use.Table)).SelectMany(p => p.Tables).Where(t => t != use.Table).ToHashSet();
                var nearApps = mates.Where(tableOf.ContainsKey).Select(t => tableOf[t].App.Id)
                    .Concat(mates.Where(globalPicks.ContainsKey).Select(t => globalPicks[t])).ToHashSet();
                options = GlobalOptions(use, candidates, claimed, pipelineFacts.Where(p => p.Tables.Contains(use.Table)).SelectMany(p => p.Hints).ToList(), nearApps, Fits,
                    binding.UnmatchedApps, appId => chosenApps.Where(kv => kv.Value.Id == appId).Select(kv => kv.Key)
                        .Concat(tableOf.Where(kv => kv.Value.App.Id == appId).Select(kv => kv.Key))
                        .Concat(globalPicks.Where(kv => kv.Value == appId).Select(kv => kv.Key)).ToList());
                if (options.Count == 1) globalPicks[use.Table] = options[0].App.Id;
            }
            if (options.Count == 0 || (options.Count > 1 && options[1].Fit.Score == options[0].Fit.Score && !SameShape(options[0].Table, options[1].Table)))
            {
                if (options.Count > 1)
                    binding.Notes.Add($"Quickbase table '{use.Table}' fits '{options[0].Table.Name}' ({options[0].App.Name}) and '{options[1].Table.Name}' ({options[1].App.Name}) equally; it was left unmatched.");
                binding.UnmatchedTables.Add(use.Table);
                continue;
            }
            appless.Add((use, options[0].App, options[0].Table, options[0].Fit));
        }
        // One existing table is the copy of one Quickbase table: when several of these tables chose it (clones in other Quickbase
        // apps), only a clearly best one keeps it; a tie keeps none.
        // Rivals count whatever app they seem to belong to: a Quickbase table that fits the same table at least as well — even one
        // with no support for that app (a clone in an app that was not imported) — makes the choice ambiguous, so whether a table
        // is matched never depends on which other apps happen to exist.
        var applessUses = uses.Values.Where(u => u.App is null).ToList();
        var allTables = candidates.SelectMany(a => a.Tables).ToList();
        string? QbAppOf(PipelineOnlyApp app) => chosenApps.FirstOrDefault(kv => kv.Value.Id == app.Id).Key;
        bool PrefixOf(string qbTable, PipelineOnlyApp app) => QbAppOf(app) is { } qbApp && CommonPrefix(qbApp, qbTable) >= 5;
        foreach (var pick in appless.ToList())
        {
            // Quickbase numbers an app's tables after the app: a table whose id starts like its app's id is that app's own table.
            if (PrefixOf(pick.Use.Table, pick.App)) continue;
            var rival = applessUses.FirstOrDefault(u => u.Table != pick.Use.Table
                && Evidence(pick.Table, u) is var fit && (fit.IsStrong || (fit.Miss == 0 && fit.Hit >= 2 && u.Fids.Count(x => x > 5) >= 2 && u.Keys.All(BuiltInKeys.Contains)))
                && fit.Score >= pick.Fit.Score && !appless.Any(a => a.Use.Table == u.Table)
                // A rival only when this table is its own best fit: a generic table that fits several tables belongs to none of them.
                && allTables.Where(t => t.Id != pick.Table.Id).All(t => Evidence(t, u).Score < fit.Score));
            if (rival is null) continue;
            appless.Remove(pick);
            binding.UnmatchedTables.Add(pick.Use.Table);
            binding.Notes.Add($"Quickbase tables {pick.Use.Table} and {rival.Table} fit '{pick.Table.Name}' ({pick.App.Name}) equally (clones of one app); none was used.");
        }
        foreach (var group in appless.GroupBy(x => x.Table.Id))
        {
            var ranked2 = group.OrderByDescending(x => x.Fit.Score).ToList();
            var keep = ranked2.Count == 1 || ranked2[0].Fit.Score > ranked2[1].Fit.Score ? ranked2[0].Use.Table : null;
            if (ranked2.Count > 1)
                binding.Notes.Add(keep is null
                    ? $"Quickbase tables {string.Join(", ", ranked2.Select(r => r.Use.Table))} all fit '{ranked2[0].Table.Name}' ({ranked2[0].App.Name}) equally (clones of one app); none was used."
                    : $"Quickbase tables {string.Join(", ", ranked2.Select(r => r.Use.Table))} all fit '{ranked2[0].Table.Name}' ({ranked2[0].App.Name}); only {keep} was used, the others are copies in other Quickbase apps.");
            foreach (var x in ranked2)
            {
                if (x.Use.Table != keep) { binding.UnmatchedTables.Add(x.Use.Table); continue; }
                tableOf[x.Use.Table] = (x.App, x.Table);
                binding.Matches.Add(new PipelineOnlyTableMatch(null, x.Use.Table, x.App.PublicId, x.App.Name, x.Table.Id, x.Table.Name));
            }
        }

        var appRefs = new Dictionary<long, string>();
        foreach (var match in binding.Matches)
        {
            var app = candidates.First(a => a.PublicId == match.AppPublicId);
            if (!appRefs.ContainsKey(app.Id)) appRefs[app.Id] = $"$App_PowerBase_{appRefs.Count + 1}";
        }

        // Apps a Quickbase app id was matched to keep their ref even when none of their tables was (DataSource::App only).
        var appByQbId = new Dictionary<string, PipelineOnlyApp>(StringComparer.Ordinal);
        foreach (var (qbApp, app) in chosenApps)
        {
            appByQbId[qbApp] = app;
            if (!appRefs.ContainsKey(app.Id)) appRefs[app.Id] = $"$App_PowerBase_{appRefs.Count + 1}";
        }

        // 3. Rewrite the pipelines' data sources into !Refs, then add the matched apps.
        var usedFields = new Dictionary<(long Table, int Fid), bool>();
        foreach (var (_, node) in resources.Children)
            if (node is YamlMappingNode resource && Scalar(resource, "Type") == "QB::Pipeline")
                RewriteDataSources(resource, tableOf, appByQbId, appRefs, binding);

        foreach (var app in candidates.Where(a => appRefs.ContainsKey(a.Id)))
        {
            var appRef = appRefs[app.Id];
            var qbTables = tableOf.Where(kv => kv.Value.App.Id == app.Id)
                .GroupBy(kv => kv.Value.Table.Id).ToDictionary(g => g.Key, g => string.Join(",", g.Select(kv => kv.Key)));
            var qbApps = string.Join(",", appByQbId.Where(kv => kv.Value.Id == app.Id).Select(kv => kv.Key));
            resources.Children[new YamlScalarNode(appRef)] = AppNode(app, qbApps, qbTables);
            binding.Apps.Add((appRef, app));
        }

        // YamlStream.Save drops the tags of mappings (!Ref, !Var): mark them with a first key and put the tag back after saving.
        MarkTags(stream.Documents[0].RootNode);
        var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var text = RestoreTags(writer.ToString());
        // YamlStream.Save ends the document with "...": harmless, but keep the file as Quickbase writes it.
        text = Regex.Replace(text, @"\r?\n\.\.\.\s*$", "\n");
        return new PipelineOnlyBinding
        {
            Content = text, Apps = binding.Apps, Matches = binding.Matches, UnmatchedTables = binding.UnmatchedTables,
            UnmatchedApps = binding.UnmatchedApps, Notes = binding.Notes,
        };
    }

    // ---------------------------------------------------------------------------------------------------------------- matching

    /// <summary>An imported table is the copy of exactly one Quickbase table. Quickbase realms often hold clones of an app
    /// (same tables, same field ids), so several Quickbase tables can fit one table equally well; only the one that fits best is
    /// kept — on a tie, the one whose id shares the longest prefix with the Quickbase app ids already matched to that app
    /// (Quickbase gives an app and the tables created with it consecutive ids). If that still ties, none is kept: a step is
    /// never pointed at a table it may not belong to.</summary>
    private static void KeepOneQuickbaseTablePerTable(Dictionary<string, TableUse> uses,
        Dictionary<string, (PipelineOnlyApp App, PipelineOnlyTable Table)> tableOf, Dictionary<string, PipelineOnlyApp> chosenApps,
        PipelineOnlyBinding binding)
    {
        foreach (var group in tableOf.GroupBy(kv => kv.Value.Table.Id).Where(g => g.Count() > 1).ToList())
        {
            var app = group.First().Value.App;
            var appIds = chosenApps.Where(kv => kv.Value.Id == app.Id).Select(kv => kv.Key).ToList();
            var ranked = group
                .Select(kv => (QbTable: kv.Key, Score: Evidence(kv.Value.Table, uses[kv.Key]).Score,
                    Own: uses[kv.Key].App is { } qbApp && appIds.Contains(qbApp) ? 1 : 0,
                    Prefix: appIds.Count == 0 ? 0 : appIds.Max(id => CommonPrefix(id, kv.Key))))
                .OrderByDescending(x => x.Own).ThenByDescending(x => x.Score).ThenByDescending(x => x.Prefix).ToList();
            var keep = ranked[0];
            var tie = ranked[1].Own == keep.Own && ranked[1].Score == keep.Score && ranked[1].Prefix == keep.Prefix;
            var table = group.First().Value.Table;
            foreach (var (qbTable, _, _, _) in tie ? ranked : ranked.Skip(1))
            {
                tableOf.Remove(qbTable);
                binding.Matches.RemoveAll(m => m.QbTableId == qbTable);
                binding.UnmatchedTables.Add(qbTable);
            }
            binding.Notes.Add(tie
                ? $"Quickbase tables {string.Join(", ", ranked.Select(r => r.QbTable))} all fit '{table.Name}' ({app.Name}) equally (clones of one app); none was used."
                : $"Quickbase tables {string.Join(", ", ranked.Select(r => r.QbTable))} all fit '{table.Name}' ({app.Name}); only {keep.QbTable} was used, the others are copies in other Quickbase apps.");
        }
    }

    private static readonly Regex AppTableLink = new(@"/nav/app/(b[a-z0-9]{8})/table/(b[a-z0-9]{8})\b", RegexOptions.Compiled);

    /// <summary>Each Quickbase table of one Quickbase app to its own table of <paramref name="app"/>, one-to-one, best fit first.
    /// A table is taken only with a usable fit (strong, or every id/key found); one that fits two different tables equally gets
    /// neither. Tables left without a table count all their evidence as missing.</summary>
    internal static (Fit Fit, Dictionary<string, PipelineOnlyTable> Tables) AssignTables(PipelineOnlyApp app, IReadOnlyList<TableUse> uses)
    {
        var options = new List<(TableUse Use, PipelineOnlyTable Table, Fit Fit)>();
        foreach (var use in uses)
        {
            var fits = app.Tables.Select(t => (Table: t, Fit: Evidence(t, use))).OrderByDescending(x => x.Fit.Score).ThenByDescending(x => x.Fit.Hit).ToList();
            if (fits.Count == 0 || !Usable(fits[0].Fit)) continue;
            // The best table must stand out: a second table that fits about as well means nothing tells them apart.
            if (fits.Count > 1 && !SameShape(fits[0].Table, fits[1].Table) && !StandsOut(fits[0].Fit, fits[1].Fit)) continue;
            options.Add((use, fits[0].Table, fits[0].Fit));
        }
        var result = new Dictionary<string, PipelineOnlyTable>(StringComparer.Ordinal);
        var taken = new HashSet<long>();
        var total = new Fit(0, 0, 0);
        foreach (var (use, table, fit) in options.OrderByDescending(o => o.Fit.Score).ThenByDescending(o => o.Fit.Hit))
        {
            if (taken.Contains(table.Id)) continue;
            result[use.Table] = table;
            taken.Add(table.Id);
            total += fit;
        }
        return (total, result);
    }

    /// <summary>App-less table, matched among all apps (2026-11-19). The best table of all apps is taken when its fit is strong
    /// and it clearly beats every other table. On a tie (clones of one app: same tables, same field ids) the tie is broken only
    /// by the apps the pipelines name (their name / tags) and then by the apps their other tables were matched to; if that still
    /// leaves more than one, nothing is matched.</summary>
    private static List<(PipelineOnlyApp App, PipelineOnlyTable Table, Fit Fit)> GlobalOptions(TableUse use, IReadOnlyList<PipelineOnlyApp> candidates,
        HashSet<long> claimed, List<string> hints, HashSet<long> nearApps, Func<Fit, bool> fits,
        IReadOnlyCollection<string> unmatchedQbApps, Func<long, List<string>> idsOf)
    {
        var named = candidates.Where(a => hints.Any(h => NamesApp(h, a.Name))).ToList();
        named = named.Where(a => !named.Any(b => b.Id != a.Id && b.Name.Length > a.Name.Length && b.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        var ranked = candidates.SelectMany(a => a.Tables.Where(t => !claimed.Contains(t.Id)).Select(t => (App: a, Table: t, Fit: Evidence(t, use))))
            .Where(x => fits(x.Fit) && x.Fit.IsStrong)
            .OrderByDescending(x => x.Fit.Score).ThenByDescending(x => x.Fit.Hit).ToList();
        Trace?.Invoke($"APPLESS {use.Table}	named={string.Join("/", named.Select(a => a.Name))}	" + string.Join(" ; ", ranked.Take(3).Select(x => $"{x.App.Name}.{x.Table.Name} h{x.Fit.Hit} m{x.Fit.Miss} k{x.Fit.KeyHits}")));
        if (ranked.Count == 0) return [];
        var top = ranked.Where(x => x.Fit.Score == ranked[0].Fit.Score).ToList();
        if (top.Count == 1)
        {
            var w = top[0];
            var supported = named.Any(n => n.Id == w.App.Id) || nearApps.Contains(w.App.Id);
            // Without the app's name or a sibling table in that app, a clone of the table in another app (whose own app was not
            // imported) would fit just as well: only a complete fit that no other table comes close to is taken.
            var runnerUp = ranked.Count > 1 ? ranked[1].Fit.Score : 0;
            var unique = w.Fit.Miss == 0 && w.Fit.KeyHits >= 3 && w.Fit.Hit >= 20 && runnerUp * 10 < w.Fit.Score * 7;
            if (!(supported || unique)) return [];
            // Some evidence missing (fields changed since the export): then it must win clearly, or a close clone in another app
            // could be the real one (its app may simply not have been imported).
            if (w.Fit.Miss > 0 && ranked.Count > 1 && (w.Fit.Score - runnerUp) * 5 < w.Fit.Score) return [];
            // ...and when the pipelines name several apps, a fit with missing evidence may belong to another named app that was not
            // imported: only a complete fit is taken then.
            if (w.Fit.Miss > 0 && named.Count > 1) return [];
            // Quickbase numbers an app's tables after the app: a table whose id is closer to a Quickbase app that matched nothing
            // (an app that was not imported) than to every Quickbase id already placed in this app belongs to that other app.
            var own = idsOf(w.App.Id);
            var ownPrefix = own.Count == 0 ? 0 : own.Max(id => CommonPrefix(id, use.Table));
            if (unmatchedQbApps.Any(id => CommonPrefix(id, use.Table) >= 5 && CommonPrefix(id, use.Table) > ownPrefix)) return [];
            return [w];
        }
        var byName = top.Where(x => named.Any(n => n.Id == x.App.Id)).ToList();
        if (byName.Count == 1) return [byName[0]];
        var pool = byName.Count > 1 ? byName : top;
        var byNear = pool.Where(x => nearApps.Contains(x.App.Id)).ToList();
        if (byNear.Count == 1) return [byNear[0]];
        return [];
    }

    /// <summary>Enough found evidence for a table: every listed id/key found, or at least one specific field key and more found
    /// than missing (Quickbase apps keep changing after their export, so some fields are always missing).</summary>
    internal static bool Usable(Fit fit) =>
        (fit.Miss == 0 && fit.Hit >= 2) || (fit.KeyHits >= 1 && fit.Hit >= MIN_STRONG_HIT && fit.Hit > fit.Miss);

    /// <summary>The best fit is clearly ahead of the runner-up.</summary>
    internal static bool StandsOut(Fit best, Fit second) => best.Score > second.Score;

    /// <summary>The pipeline's name and tags: Quickbase users name a pipeline after its app ("... (CARES Database)") or tag it.</summary>
    private static List<string> PipelineHints(YamlMappingNode pipeline, YamlMappingNode? parameters)
    {
        var hints = new List<string>();
        if (Child(pipeline, "Properties") is not YamlMappingNode props) return hints;
        if (Child(props, "Tags") is YamlSequenceNode tags)
            hints.AddRange(tags.Children.OfType<YamlScalarNode>().Select(t => t.Value ?? "").Where(t => t.Length > 0));
        switch (Child(props, "Name"))
        {
            case YamlScalarNode { Value: { } name }: hints.Add(name); break;
            case YamlMappingNode var when Scalar(var, "Name") is { } param && parameters is not null
                && Child(parameters, param) is YamlMappingNode def && Scalar(def, "Value") is { } value:
                hints.Add(value); break;
        }
        return hints;
    }

    /// <summary>The text names the app as a whole word run ("CARES Database" in "Fix (CARES Database)", not in "CARES Database 2").</summary>
    internal static bool NamesApp(string text, string appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;
        return Regex.IsMatch(text, @"(?<![A-Za-z0-9])" + Regex.Escape(appName.Trim()) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n;
    }

    internal static (PipelineOnlyTable? Table, int Score) BestIn(PipelineOnlyApp app, TableUse use)
    {
        PipelineOnlyTable? best = null;
        int bestScore = int.MinValue, second = int.MinValue;
        foreach (var table in app.Tables)
        {
            var score = Score(table, use);
            if (score > bestScore) { second = bestScore; bestScore = score; best = table; }
            else if (score > second) second = score;
        }
        // Two tables of one app that fit equally well (copies of a table): nothing tells them apart, so neither is used.
        if (best is null || bestScore <= 0 || bestScore == second) return (null, bestScore == int.MinValue ? 0 : bestScore);
        return (best, bestScore);
    }

    /// <summary>How well a table fits what the steps use: a field key (<c>employee_name</c>) is specific, a field id is not
    /// (almost every table has fields 6-10), so a found key weighs 3 and a found id 1; a missing key or id weighs 3 and 2.
    /// Ids 1-5 and the keys every table has are left out: they fit every table.</summary>
    internal readonly record struct Fit(int Hit, int Miss, int KeyHits)
    {
        public int Score => Hit - Miss;
        /// <summary>Enough evidence, nearly all of it found, and at least one field key (ids alone fit nearly any table).</summary>
        public bool IsStrong => KeyHits > 0 && Hit >= MIN_STRONG_HIT && Miss * 5 <= Hit;
        public static Fit operator +(Fit a, Fit b) => new(a.Hit + b.Hit, a.Miss + b.Miss, a.KeyHits + b.KeyHits);
    }

    private const int MIN_STRONG_HIT = 4;

    internal static Fit Evidence(PipelineOnlyTable table, TableUse use)
    {
        var memo = _evidenceMemo;
        if (memo is not null && memo.TryGetValue((table, use), out var cached)) return cached;
        var index = IndexOf(table);
        int hit = 0, miss = 0, keys = 0;
        foreach (var fid in use.Fids.Where(f => f > 5))
            if (index.Fids.Contains(fid)) hit += 1; else miss += 2;
        foreach (var key in use.Keys.Where(k => !BuiltInKeys.Contains(k)))
            if (index.HasKey(key))
            {
                if (_commonKeys?.IsCommon(key) == true) hit += 1;
                else { hit += 3; keys++; }
            }
            else miss += 3;
        var fit = new Fit(hit, miss, keys);
        memo?.TryAdd((table, use), fit);
        return fit;
    }

    internal static int Score(PipelineOnlyTable table, TableUse use) => Evidence(table, use).Score;

    /// <summary>How well an app fits all tables of one Quickbase app: each table's best table in the app (a table with no
    /// usable fit counts all its evidence as missing).</summary>
    internal static Fit AppFit(PipelineOnlyApp app, IEnumerable<TableUse> uses)
    {
        var total = new Fit(0, 0, 0);
        foreach (var use in uses)
        {
            var (table, _) = BestIn(app, use);
            total += table is not null ? Evidence(table, use) : MissingAll(use);
        }
        return total;
    }

    private static Fit MissingAll(TableUse use) =>
        new(0, use.Fids.Count(f => f > 5) * 2 + use.Keys.Count(k => !BuiltInKeys.Contains(k)) * 3, 0);

    /// <summary>Two existing apps with the same tables (each a <see cref="SameShape"/> copy): one app imported twice.</summary>
    private static bool IsCopyOf(PipelineOnlyApp a, PipelineOnlyApp b) =>
        a.Tables.Count == b.Tables.Count && a.Tables.Count > 0 && a.Tables.All(t => b.Tables.Any(u => SameShape(t, u)));

    /// <summary>Copies of one table (same name, field ids and labels), e.g. an app imported twice.</summary>
    private static bool SameShape(PipelineOnlyTable a, PipelineOnlyTable b) =>
        a.Name == b.Name && a.Fields.Select(f => (f.Fid, f.Label)).ToHashSet().SetEquals(b.Fields.Select(f => (f.Fid, f.Label)));

    internal static PipelineOnlyField? FieldByKey(PipelineOnlyTable table, string key)
    {
        var index = IndexOf(table);
        var wanted = QblPipelineConverter.NormalizeKey(key);
        return index.ByLabel.TryGetValue(wanted, out var field) ? field
            : index.ByCompact.TryGetValue(wanted.Replace("_", "", StringComparison.Ordinal), out field) ? field : null;
    }

    /// <summary>A table's field ids and normalized labels, worked out once per table (normalizing every label on every comparison
    /// made matching a large file slow). Same answers as scanning the fields in order: the first field with a label wins.</summary>
    private sealed class TableIndex
    {
        public required HashSet<int> Fids { get; init; }
        public Dictionary<string, PipelineOnlyField> ByLabel { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PipelineOnlyField> ByCompact { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AddressParts { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _keys = new(StringComparer.Ordinal);

        public bool HasKey(string key)
        {
            if (_keys.TryGetValue(key, out var has)) return has;
            var wanted = QblPipelineConverter.NormalizeKey(key);
            var compact = wanted.Replace("_", "", StringComparison.Ordinal);
            has = ByLabel.ContainsKey(wanted) || ByCompact.ContainsKey(compact) || AddressParts.Contains(compact);
            lock (_keys) _keys[key] = has;
            return has;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PipelineOnlyTable, TableIndex> Indexes = new();

    private static TableIndex IndexOf(PipelineOnlyTable table) => Indexes.GetValue(table, t =>
    {
        var index = new TableIndex { Fids = t.Fields.Select(f => f.Fid).Concat(PartIds(t).Keys).ToHashSet() };
        foreach (var f in t.Fields)
        {
            var label = QblPipelineConverter.NormalizeKey(string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label);
            index.ByLabel.TryAdd(label, f);
            index.ByCompact.TryAdd(label.Replace("_", "", StringComparison.Ordinal), f);
        }
        foreach (var address in t.Fields.Where(f => f.TypeCode == "Address"))
            foreach (var (_, _, part) in AddressParts)
                index.AddressParts.Add(QblPipelineConverter.NormalizeKey((address.Label ?? address.Name) + " " + part).Replace("_", "", StringComparison.Ordinal));
        return index;
    });

    // --------------------------------------------------------------------------------------------------------------- collecting

    private static HashSet<string> CollectPipeline(YamlMappingNode pipeline, Dictionary<string, TableUse> uses)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var steps = new List<StepInfo>();
        Walk(pipeline, steps);
        var byId = steps.Where(s => s.Id is not null).GroupBy(s => s.Id!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Tokens ({{aa.t_rating}}) anywhere in the pipeline name a field of step aa's table.
        foreach (var scalar in AllScalars(pipeline))
            foreach (Match match in Token.Matches(scalar))
                if (byId.TryGetValue(match.Groups[1].Value, out var step))
                    step.Keys.Add(match.Groups[2].Value);

        string? TableOf(StepInfo step, int depth = 0) =>
            step.Table ?? (depth < 10 && step.TargetId is not null && byId.TryGetValue(step.TargetId, out var target) ? TableOf(target, depth + 1) : null);

        foreach (var step in steps)
            foreach (var (table, app, fids) in step.Tables)
            {
                var use = Use(uses, table);
                tables.Add(table);
                use.App ??= app;
                foreach (var fid in fids) use.Fids.Add(fid);
            }
        foreach (var step in steps)
            if (TableOf(step) is { } table)
            {
                var use = Use(uses, table);
                use.App ??= step.App ?? (step.TargetId is not null && byId.TryGetValue(step.TargetId, out var t) ? t.App : null);
                tables.Add(table);
                foreach (var key in step.Keys) use.Keys.Add(key);
            }
        return tables;
    }

    private static TableUse Use(Dictionary<string, TableUse> uses, string table)
    {
        if (!uses.TryGetValue(table, out var use)) uses[table] = use = new TableUse(table);
        return use;
    }

    /// <summary>Every pipeline step below <paramref name="node"/> (Steps, Then, Else, Try, OnError, OnSuccess…).</summary>
    private static void Walk(YamlNode node, List<StepInfo> steps)
    {
        switch (node)
        {
            case YamlMappingNode map:
                if (Scalar(map, "Type") is { } type && type.StartsWith("QB::PipelineStep::", StringComparison.Ordinal)
                    && !type.StartsWith("QB::PipelineStep::DataSource", StringComparison.Ordinal) && type != "QB::PipelineStep::InputsMeta")
                    steps.Add(ReadStep(map));
                foreach (var (_, child) in map.Children) Walk(child, steps);
                break;
            case YamlSequenceNode seq:
                foreach (var child in seq.Children) Walk(child, steps);
                break;
        }
    }

    private static StepInfo ReadStep(YamlMappingNode step)
    {
        var info = new StepInfo();
        if (Child(step, "Properties") is YamlMappingNode properties)
        {
            info.Id = Scalar(properties, "Id");
            if (Child(properties, "Target") is YamlMappingNode target) info.TargetId = Scalar(target, "StepId");
            if (Child(properties, "Inputs") is YamlMappingNode inputs)
            {
                foreach (var (key, _) in inputs.Children)
                    if (key is YamlScalarNode k && k.Value is { } name) info.Keys.Add(name);
                // A Make Request calling the Quickbase API names its tables and field ids in the URL / body.
                foreach (var (qbTable, fids) in QuickbaseApiUrlMap.Evidence(Scalar(inputs, "Url"), Scalar(inputs, "Body")))
                    info.Tables.Add((qbTable, null, fids.ToList()));
            }
            if (Child(properties, "Filters") is { } filters)
                foreach (var map in Mappings(filters))
                    if (Scalar(map, "Field") is { } field && Scalar(map, "Target") is null) info.Keys.Add(field);
        }
        var sources = DataSourcesByKey(step);
        foreach (var (key, source) in sources)
        {
            if (Scalar(source, "Type") != "QB::PipelineStep::DataSource::Table" || Child(source, "Properties") is not YamlMappingNode tp
                || Child(tp, "Table") is not YamlScalarNode { Value: { } qbTable }) continue;
            string? qbApp = null;
            if (Child(tp, "Source") is YamlMappingNode src && Scalar(src, "PipelineStep_DataSource") is { } appKey && sources.TryGetValue(appKey, out var appSource)
                && Child(appSource, "Properties") is YamlMappingNode ap && Child(ap, "App") is YamlScalarNode { Value: { } appId })
                qbApp = appId;
            var fids = new List<int>();
            foreach (var (_, other) in sources)
                if (Scalar(other, "Type") == "QB::PipelineStep::DataSource::Fields" && Child(other, "Properties") is YamlMappingNode fp
                    && Child(fp, "Source") is YamlMappingNode fs && Scalar(fs, "PipelineStep_DataSource") == key && Child(fp, "Fields") is YamlSequenceNode list)
                    fids.AddRange(list.Children.OfType<YamlScalarNode>().Select(n => int.TryParse(n.Value, out var f) ? f : 0).Where(f => f > 0));
            info.Tables.Add((qbTable, qbApp, fids));
        }
        foreach (var (_, source) in sources)
        {
            var type = Scalar(source, "Type");
            if (Child(source, "Properties") is not YamlMappingNode props) continue;
            var name = Scalar(props, "Name");
            if (type == "QB::PipelineStep::DataSource::App" && Child(props, "App") is YamlScalarNode app) info.App ??= app.Value;
            // The step's own table is "table"; Copy Records also has "source_table".
            if (type == "QB::PipelineStep::DataSource::Table" && Child(props, "Table") is YamlScalarNode table && name == "table")
                info.Table = table.Value;
        }
        return info;
    }

    private static Dictionary<string, YamlMappingNode> DataSourcesByKey(YamlMappingNode step)
    {
        var result = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
        if (Child(step, "InputsMeta") is not YamlMappingNode metas) return result;
        foreach (var (_, meta) in metas.Children)
            if (meta is YamlMappingNode m && Child(m, "PipelineStep_DataSources") is YamlMappingNode sources)
                foreach (var (k, source) in sources.Children)
                    if (k is YamlScalarNode { Value: { } name } && source is YamlMappingNode s) result[name] = s;
        return result;
    }

    /// <summary>The data sources of a step (InputsMeta → * → PipelineStep_DataSources → *), not of its nested steps.</summary>
    private static IEnumerable<YamlMappingNode> DataSources(YamlMappingNode step)
    {
        if (Child(step, "InputsMeta") is not YamlMappingNode metas) yield break;
        foreach (var (_, meta) in metas.Children)
            if (meta is YamlMappingNode m && Child(m, "PipelineStep_DataSources") is YamlMappingNode sources)
                foreach (var (_, source) in sources.Children)
                    if (source is YamlMappingNode s) yield return s;
    }

    // ---------------------------------------------------------------------------------------------------------------- rewriting

    private static void RewriteDataSources(YamlNode node, Dictionary<string, (PipelineOnlyApp App, PipelineOnlyTable Table)> tableOf,
        Dictionary<string, PipelineOnlyApp> appByQbId, Dictionary<long, string> appRefs, PipelineOnlyBinding binding)
    {
        switch (node)
        {
            case YamlMappingNode map:
                if (Scalar(map, "Type") is { } type && type.StartsWith("QB::PipelineStep::", StringComparison.Ordinal)
                    && !type.StartsWith("QB::PipelineStep::DataSource", StringComparison.Ordinal) && type != "QB::PipelineStep::InputsMeta")
                    RewriteStep(map, tableOf, appByQbId, appRefs, binding);
                foreach (var (_, child) in map.Children.ToList()) RewriteDataSources(child, tableOf, appByQbId, appRefs, binding);
                break;
            case YamlSequenceNode seq:
                foreach (var child in seq.Children.ToList()) RewriteDataSources(child, tableOf, appByQbId, appRefs, binding);
                break;
        }
    }

    private static void RewriteStep(YamlMappingNode step, Dictionary<string, (PipelineOnlyApp App, PipelineOnlyTable Table)> tableOf,
        Dictionary<string, PipelineOnlyApp> appByQbId, Dictionary<long, string> appRefs, PipelineOnlyBinding binding)
    {
        if (Child(step, "InputsMeta") is not YamlMappingNode metas) return;
        foreach (var (_, meta) in metas.Children)
        {
            if (meta is not YamlMappingNode m || Child(m, "PipelineStep_DataSources") is not YamlMappingNode sources) continue;
            var byKey = sources.Children.Where(kv => kv.Key is YamlScalarNode && kv.Value is YamlMappingNode)
                .ToDictionary(kv => ((YamlScalarNode)kv.Key).Value!, kv => (YamlMappingNode)kv.Value, StringComparer.Ordinal);

            // Table data sources first: field lists point at them.
            var tableBySource = new Dictionary<string, (PipelineOnlyApp App, PipelineOnlyTable Table)>(StringComparer.Ordinal);
            foreach (var (key, source) in byKey)
            {
                if (Scalar(source, "Type") != "QB::PipelineStep::DataSource::Table" || Child(source, "Properties") is not YamlMappingNode props
                    || Child(props, "Table") is not YamlScalarNode table || table.Value is null || !tableOf.TryGetValue(table.Value, out var found))
                    continue;
                tableBySource[key] = found;
                props.Children[new YamlScalarNode("Table")] = RefNode(("App", appRefs[found.App.Id]), ("Table", TableRef(found.Table)));
            }
            foreach (var (_, source) in byKey)
            {
                if (Child(source, "Properties") is not YamlMappingNode props) continue;
                switch (Scalar(source, "Type"))
                {
                    case "QB::PipelineStep::DataSource::App" when Child(props, "App") is YamlScalarNode app && app.Value is { } qbApp:
                        var target = appByQbId.TryGetValue(qbApp, out var a) ? a
                            : tableBySource.Values.Select(t => t.App).FirstOrDefault();
                        if (target is not null)
                            props.Children[new YamlScalarNode("App")] = RefNode(("App", appRefs[target.Id]));
                        break;
                    case "QB::PipelineStep::DataSource::Fields" when Child(props, "Fields") is YamlSequenceNode fields:
                        var sourceKey = Child(props, "Source") is YamlMappingNode src ? Scalar(src, "PipelineStep_DataSource") : null;
                        if (sourceKey is null || !tableBySource.TryGetValue(sourceKey, out var owner)) break;
                        var fids = owner.Table.Fields.Select(f => f.Fid).Concat(PartIds(owner.Table).Keys).ToHashSet();
                        var list = new YamlSequenceNode();
                        var dropped = new List<string>();
                        foreach (var item in fields.Children)
                        {
                            if (item is YamlScalarNode s && int.TryParse(s.Value, out var fid))
                            {
                                if (fids.Contains(fid))
                                    list.Add(RefNode(("App", appRefs[owner.App.Id]), ("Table", TableRef(owner.Table)), ("Field", FieldRef(fid))));
                                else dropped.Add(s.Value!);
                            }
                            else list.Add(item);
                        }
                        props.Children[new YamlScalarNode("Fields")] = list;
                        if (dropped.Count > 0)
                        {
                            var note = $"Field ids {string.Join(", ", dropped)} listed for table '{owner.Table.Name}' ({owner.App.Name}) are not in that table and were left out.";
                            if (!binding.Notes.Contains(note)) binding.Notes.Add(note);
                        }
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------ synthetic app

    private static string TableRef(PipelineOnlyTable table) => $"$Table_PB_{table.Id}";

    /// <summary>The Quickbase ids of address parts. Quickbase gives an Address field's six parts the next six field ids
    /// (Address 37 → Street 1 38, Street 2 39, City 40, State 41, Postal Code 42, Country 43); PowerBase keeps them inside the
    /// Address field, so they have no Fid. An id is only taken when the table has no field of its own with it.</summary>
    internal static Dictionary<int, (int Address, string Property)> PartIds(PipelineOnlyTable table)
    {
        var taken = table.Fields.Select(f => f.Fid).ToHashSet();
        var result = new Dictionary<int, (int, string)>();
        foreach (var address in table.Fields.Where(f => f.TypeCode == "Address"))
            for (var i = 0; i < AddressParts.Length; i++)
            {
                var id = address.Fid + i + 1;
                if (!taken.Contains(id)) result.TryAdd(id, (address.Fid, AddressParts[i].Property));
            }
        return result;
    }

    /// <summary>A part with a known Quickbase id is <c>$Field_40</c> (what the rewritten field lists point at).</summary>
    private static string PartRef(int addressFid, string property, Dictionary<int, (int Address, string Property)> partIds) =>
        partIds.FirstOrDefault(kv => kv.Value == (addressFid, property)).Key is var id and > 0 ? FieldRef(id) : $"{FieldRef(addressFid)}_{property}";
    private static string FieldRef(int fid) => $"$Field_{fid}";

    private static YamlMappingNode AppNode(PipelineOnlyApp app, string qbAppIds, IReadOnlyDictionary<long, string> qbTableIds)
    {
        var tables = new YamlMappingNode();
        foreach (var table in app.Tables)
            tables.Add(TableRef(table), TableNode(table, qbTableIds.GetValueOrDefault(table.Id)));
        var properties = new YamlMappingNode { { "Name", Plain(app.Name) } };
        if (qbAppIds.Length > 0) properties.Add("QbAppIds", qbAppIds);
        return new YamlMappingNode
        {
            { "Type", "QB::Application" },
            { "Properties", properties },
            { "Tables", tables },
        };
    }

    private static YamlMappingNode TableNode(PipelineOnlyTable table, string? qbTableIds)
    {
        var properties = new YamlMappingNode { { "Name", Plain(table.Name) } };
        if (!string.IsNullOrEmpty(qbTableIds)) properties.Add("QbTableIds", qbTableIds);
        if (table.KeyFid is { } key && table.Fields.Any(f => f.Fid == key))
            properties.Add("KeyField", RefNode(("Field", FieldRef(key))));
        var fields = new YamlMappingNode();
        var partIds = PartIds(table);
        foreach (var field in table.Fields)
        {
            var type = QbType(field);
            var fieldProps = new YamlMappingNode { { "Label", Plain(string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label) } };
            if (type == "QB::Field::Address")
                foreach (var (property, _, _) in AddressParts)
                    fieldProps.Add(property, RefNode(("Field", PartRef(field.Fid, property, partIds))));
            fields.Add(FieldRef(field.Fid), new YamlMappingNode { { "Type", type }, { "Id", field.Fid.ToString() }, { "Properties", fieldProps } });
            if (type == "QB::Field::Address")
                foreach (var (property, partType, label) in AddressParts)
                {
                    var part = new YamlMappingNode { { "Type", partType } };
                    if (partIds.FirstOrDefault(kv => kv.Value == (field.Fid, property)).Key is var partId and > 0)
                        part.Add("Id", partId.ToString());
                    part.Add("Properties", new YamlMappingNode { { "Label", label } });
                    fields.Add(PartRef(field.Fid, property, partIds), part);
                }
        }
        return new YamlMappingNode { { "Type", "QB::Table" }, { "Properties", properties }, { "Fields", fields } };
    }

    private static readonly Dictionary<string, string> TypeByCode = QblFieldTypeMap.ScalarTypes
        .GroupBy(kv => kv.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

    /// <summary>The Quickbase field type of a PowerBase field, as far as the pipeline converter cares (system, user, computed…).</summary>
    internal static string QbType(PipelineOnlyField field)
    {
        if (field.IsSystem)
            switch (field.Fid)
            {
                case 1: return "QB::Field::DateCreated";
                case 2: return "QB::Field::DateModified";
                case 3: return "QB::Field::RecordID";
                case 4: return "QB::Field::RecordOwner";
                case 5: return "QB::Field::LastModifiedBy";
            }
        if (TypeByCode.TryGetValue(field.TypeCode, out var scalar)) return scalar;
        return field.TypeCode switch
        {
            "Formula_Bool" => "QB::Field::Checkbox::Formula",
            "Formula_Date" => "QB::Field::Date::Formula",
            "Formula_DateTime" => "QB::Field::DateTime::Formula",
            "Formula_Duration" => "QB::Field::Duration::Formula",
            "Formula_Email" => "QB::Field::Email::Formula",
            "Formula_Number" => "QB::Field::Numeric::Formula",
            "Formula_Phone" => "QB::Field::PhoneNumber::Formula",
            "Formula_RichText" => "QB::Field::RichText::Formula",
            "Formula_Url" => "QB::Field::URL::Formula",
            "Formula_User" => "QB::Field::User::Formula",
            "Formula_ListUser" => "QB::Field::User::Formula",
            "Formula_MultiSelect" => "QB::Field::MultiselectText::Formula",
            "Formula_Time" => "QB::Field::TimeOfDay::Formula",
            // A Lookup / Summary is calculated (never written): typed as a formula of what it holds, so the converter keeps it
            // read-only and gives its filters that type's operators (a date lookup compares dates).
            "Lookup" or "Summary" when field.ResultTypeCode is { } result && ComputedOf(result) is { } computed => computed,
            "Lookup" => "QB::Field::Lookup",
            "Summary" => "QB::Field::Summary",
            "Reference" => "QB::Field::Numeric",
            "ReportLink" => "QB::Field::ReportLink",
            _ when field.TypeCode.StartsWith("Formula_", StringComparison.Ordinal) => "QB::Field::Text::Formula",
            _ => "QB::Field::Text",
        };
    }

    private static string? ComputedOf(string resultTypeCode) => resultTypeCode switch
    {
        "Text" or "TextMultiLine" or "SingleSelect" => "QB::Field::Text::Formula",
        "RichText" => "QB::Field::RichText::Formula",
        "MultiSelect" => "QB::Field::MultiselectText::Formula",
        "Number" or "Rating" or "Reference" => "QB::Field::Numeric::Formula",
        "Currency" => "QB::Field::Currency::Formula",
        "Percent" => "QB::Field::Percent::Formula",
        "Date" => "QB::Field::Date::Formula",
        "DateTime" => "QB::Field::DateTime::Formula",
        "Time" => "QB::Field::TimeOfDay::Formula",
        "Duration" => "QB::Field::Duration::Formula",
        "Boolean" => "QB::Field::Checkbox::Formula",
        "Email" => "QB::Field::Email::Formula",
        "Phone" => "QB::Field::PhoneNumber::Formula",
        "Url" => "QB::Field::URL::Formula",
        "User" or "MultiUser" => "QB::Field::User::Formula",
        _ when resultTypeCode.StartsWith("Formula_", StringComparison.Ordinal) => ComputedOf(resultTypeCode["Formula_".Length..] switch
        {
            "Bool" => "Boolean", "ListUser" => "MultiUser", var other => other,
        }),
        _ => null,
    };

    // ------------------------------------------------------------------------------------------------------------------ helpers

    private const string TagKey = "__qbl_tag__";
    private static readonly Regex TaggedKey = new(@"^(?<indent>[ ]*)(?<key>[^\s#][^\r\n]*?):\r?\n[ ]*" + TagKey + @": (?<tag>\S+)\r?$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex TaggedItem = new(@"^(?<indent>[ ]*)- " + TagKey + @": (?<tag>\S+)\r?$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static void MarkTags(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var (_, child) in map.Children.ToList()) MarkTags(child);
                if (!map.Tag.IsEmpty && map.Tag.Value.StartsWith('!') && map.Children.Count > 0)
                {
                    var entries = map.Children.ToList();
                    map.Children.Clear();
                    map.Add(TagKey, map.Tag.Value[1..]);
                    foreach (var (k, v) in entries) map.Add(k, v);
                }
                break;
            case YamlSequenceNode seq:
                foreach (var child in seq.Children) MarkTags(child);
                break;
        }
    }

    private static string RestoreTags(string text)
    {
        text = TaggedKey.Replace(text, m => $"{m.Groups["indent"].Value}{m.Groups["key"].Value}: !{m.Groups["tag"].Value}");
        return TaggedItem.Replace(text, m => $"{m.Groups["indent"].Value}- !{m.Groups["tag"].Value}");
    }

    private static YamlScalarNode Plain(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };

    private static YamlMappingNode RefNode(params (string Key, string Value)[] pairs)
    {
        var node = new YamlMappingNode { Tag = RefTag };
        foreach (var (key, value) in pairs) node.Add(key, value);
        return node;
    }

    private static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Scalar(YamlMappingNode map, string key) => Child(map, key) is YamlScalarNode s ? s.Value : null;

    private static IEnumerable<YamlMappingNode> Mappings(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                yield return map;
                foreach (var (_, child) in map.Children)
                    foreach (var inner in Mappings(child)) yield return inner;
                break;
            case YamlSequenceNode seq:
                foreach (var child in seq.Children)
                    foreach (var inner in Mappings(child)) yield return inner;
                break;
        }
    }

    private static IEnumerable<string> AllScalars(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode s when s.Value is not null:
                yield return s.Value;
                break;
            case YamlMappingNode map:
                foreach (var (_, child) in map.Children)
                    foreach (var value in AllScalars(child)) yield return value;
                break;
            case YamlSequenceNode seq:
                foreach (var child in seq.Children)
                    foreach (var value in AllScalars(child)) yield return value;
                break;
        }
    }
}
