using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PowerBase.Application.Import.Qbl;

/// <summary>A field of an existing PowerBase table, as the pipeline-only import sees it.</summary>
public sealed record PipelineOnlyField(int Fid, string Name, string Label, string TypeCode, bool IsSystem);

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
        var stream = new YamlStream();
        using (var reader = new StringReader(content))
            stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
            || Child(root, "Resources") is not YamlMappingNode resources)
            return new PipelineOnlyBinding { Content = content };

        // 1. What each Quickbase table is used for.
        var uses = new Dictionary<string, TableUse>(StringComparer.Ordinal);
        foreach (var (_, node) in resources.Children)
            if (node is YamlMappingNode resource && Scalar(resource, "Type") == "QB::Pipeline")
                CollectPipeline(resource, uses);

        // 2. Match apps first (all tables of one Quickbase app go to one app), then each table inside its app.
        var binding = new PipelineOnlyBinding();
        var appRefs = new Dictionary<long, string>();
        var tableOf = new Dictionary<string, (PipelineOnlyApp App, PipelineOnlyTable Table)>(StringComparer.Ordinal);
        var chosenApps = new Dictionary<string, PipelineOnlyApp>(StringComparer.Ordinal);

        foreach (var group in uses.Values.Where(u => u.App is not null).GroupBy(u => u.App!, StringComparer.Ordinal))
        {
            var best = candidates
                .Select(app => (App: app, Score: group.Sum(use => Math.Max(0, BestIn(app, use).Score))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.App.Id)
                .ToList();
            if (best.Count == 0) continue;
            chosenApps[group.Key] = best[0].App;
            if (best.Count > 1 && best[1].Score == best[0].Score)
                binding.Notes.Add($"Quickbase app '{group.Key}' matches several apps equally ({best[0].App.Name}, {best[1].App.Name}); the newest, '{best[0].App.Name}', was used.");
        }

        foreach (var use in uses.Values)
        {
            // A table named without its app (Copy Records' source table): look in the apps already chosen first, then everywhere.
            IEnumerable<PipelineOnlyApp> scope = use.App is not null && chosenApps.TryGetValue(use.App, out var chosen) ? [chosen]
                : use.App is null && chosenApps.Count > 0 ? chosenApps.Values.Distinct() : candidates;
            var ranked = scope.Select(app => (App: app, Match: BestIn(app, use))).Where(x => x.Match.Table is not null && x.Match.Score > 0)
                .OrderByDescending(x => x.Match.Score).ThenByDescending(x => x.App.Id).ToList();
            if (ranked.Count == 0 && use.App is null && chosenApps.Count > 0)
                ranked = candidates.Select(app => (App: app, Match: BestIn(app, use))).Where(x => x.Match.Table is not null && x.Match.Score > 0)
                    .OrderByDescending(x => x.Match.Score).ThenByDescending(x => x.App.Id).ToList();
            if (ranked.Count == 0)
            {
                binding.UnmatchedTables.Add(use.Table);
                continue;
            }
            var (app, match) = ranked[0];
            tableOf[use.Table] = (app, match.Table!);
            if (!appRefs.ContainsKey(app.Id))
                appRefs[app.Id] = $"$App_PowerBase_{appRefs.Count + 1}";
            binding.Matches.Add(new PipelineOnlyTableMatch(use.App, use.Table, app.PublicId, app.Name, match.Table!.Id, match.Table.Name));
        }

        // Apps a Quickbase app id was matched to keep their ref even when none of their tables was (DataSource::App only).
        var appByQbId = new Dictionary<string, PipelineOnlyApp>(StringComparer.Ordinal);
        foreach (var (qbApp, app) in chosenApps)
        {
            appByQbId[qbApp] = app;
            if (!appRefs.ContainsKey(app.Id)) appRefs[app.Id] = $"$App_PowerBase_{appRefs.Count + 1}";
        }
        foreach (var (qbTable, (app, _)) in tableOf)
            if (uses[qbTable].App is { } qbApp) appByQbId.TryAdd(qbApp, app);

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
            Content = text, Apps = binding.Apps, Matches = binding.Matches, UnmatchedTables = binding.UnmatchedTables, Notes = binding.Notes,
        };
    }

    // ---------------------------------------------------------------------------------------------------------------- matching

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

    /// <summary>+2 for every listed field id the table has, -2 for every one it lacks; +3 for every field key it has, -3 for every
    /// one it lacks. Ids 1-5 and the keys every table has are left out: they fit every table.</summary>
    internal static int Score(PipelineOnlyTable table, TableUse use)
    {
        var fids = table.Fields.Select(f => f.Fid).Concat(PartIds(table).Keys).ToHashSet();
        var score = 0;
        foreach (var fid in use.Fids.Where(f => f > 5))
            score += fids.Contains(fid) ? 2 : -2;
        foreach (var key in use.Keys.Where(k => !BuiltInKeys.Contains(k)))
            score += FieldByKey(table, key) is not null || AddressPartByKey(table, key) is not null ? 3 : -3;
        return score;
    }

    internal static PipelineOnlyField? FieldByKey(PipelineOnlyTable table, string key)
    {
        var wanted = QblPipelineConverter.NormalizeKey(key);
        var compact = wanted.Replace("_", "", StringComparison.Ordinal);
        string Label(PipelineOnlyField f) => QblPipelineConverter.NormalizeKey(string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label);
        return table.Fields.FirstOrDefault(f => Label(f) == wanted)
               ?? table.Fields.FirstOrDefault(f => Label(f).Replace("_", "", StringComparison.Ordinal) == compact);
    }

    private static PipelineOnlyField? AddressPartByKey(PipelineOnlyTable table, string key)
    {
        var compact = QblPipelineConverter.NormalizeKey(key).Replace("_", "", StringComparison.Ordinal);
        foreach (var address in table.Fields.Where(f => f.TypeCode == "Address"))
            foreach (var (_, _, label) in AddressParts)
                if (QblPipelineConverter.NormalizeKey((address.Label ?? address.Name) + " " + label).Replace("_", "", StringComparison.Ordinal) == compact)
                    return address;
        return null;
    }

    // --------------------------------------------------------------------------------------------------------------- collecting

    private static void CollectPipeline(YamlMappingNode pipeline, Dictionary<string, TableUse> uses)
    {
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
                use.App ??= app;
                foreach (var fid in fids) use.Fids.Add(fid);
            }
        foreach (var step in steps)
            if (TableOf(step) is { } table)
            {
                var use = Use(uses, table);
                use.App ??= step.App ?? (step.TargetId is not null && byId.TryGetValue(step.TargetId, out var t) ? t.App : null);
                foreach (var key in step.Keys) use.Keys.Add(key);
            }
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
                foreach (var (key, _) in inputs.Children)
                    if (key is YamlScalarNode k && k.Value is { } name) info.Keys.Add(name);
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
            "Lookup" => "QB::Field::Lookup",
            "Summary" => "QB::Field::Summary",
            "Reference" => "QB::Field::Numeric",
            "ReportLink" => "QB::Field::ReportLink",
            _ when field.TypeCode.StartsWith("Formula_", StringComparison.Ordinal) => "QB::Field::Text::Formula",
            _ => "QB::Field::Text",
        };
    }

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
