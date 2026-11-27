using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PowerBase.Application.Import.Qbl;

/// <summary>A field of an existing PowerBase table, as the pipeline-only import sees it.</summary>
/// <remarks><see cref="ResultTypeCode"/>: what a Lookup / Summary field holds (its settings' resultTypeCode), so filters on it get
/// the operators of that type.</remarks>
public sealed record PipelineOnlyField(int Fid, string Name, string Label, string TypeCode, bool IsSystem, string? ResultTypeCode = null);

/// <summary>A table of an existing PowerBase app. <see cref="KeyFid"/> is its Set-Key field (null = Record ID#).</summary>
public sealed record PipelineOnlyTable(long Id, string Name, int? KeyFid, IReadOnlyList<PipelineOnlyField> Fields, string? QbTableId = null);

/// <summary>An existing PowerBase app the pipelines of a pipeline-only QBL file may belong to.</summary>
public sealed record PipelineOnlyApp(Guid PublicId, long Id, string Name, IReadOnlyList<PipelineOnlyTable> Tables, string? QbAppId = null);

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
    /// <summary>Pipelines of the file left out because one of their Quickbase app / table ids is not among the imported apps.</summary>
    public List<(string Name, string Reason)> SkippedPipelines { get; init; } = [];
}

/// <summary>
/// A Quickbase export of pipelines only (no <c>QB::Application</c>) names apps, tables and fields by their Quickbase ids
/// (<c>App: bt2kqxkqk</c>, <c>Table: bwcrtu9k5</c>, <c>Fields: [7, 20]</c>) instead of <c>!Ref</c>s into the file. Quickbase
/// application import stores each app's and table's Quickbase id (meta.App.QBAppID, meta.AppTable.QBTableID), so each
/// Quickbase id of the file is looked up exactly; an imported field's Fid is its Quickbase field id. The file is then rewritten as if it had been exported with those apps,
/// so the normal pipeline converter and importer handle it — nothing in the apps is created or changed.
/// </summary>
public static class QblPipelineOnlyBinder
{
    private static readonly Regex ApplicationLine = new(@"^\s*Type:\s*QB::Application\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PipelineLine = new(@"^\s*Type:\s*QB::Pipeline\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Token = new(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\.([A-Za-z0-9_]+)", RegexOptions.Compiled);

    private static readonly TagName RefTag = new("!Ref");

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

    /// <summary>Diagnostics only (tests): receives one line per pipeline looked up.</summary>
    [ThreadStatic] public static Action<string>? Trace;

    /// <summary>
    /// Matches the file to <paramref name="candidates"/> by the Quickbase ids stored when the apps were imported
    /// (<c>meta.App.QBAppID</c>, <c>meta.AppTable.QBTableID</c>) — never by names or fields. A pipeline is imported only when
    /// EVERY Quickbase app id and table id it uses is found; any other pipeline is left out of the file and reported in
    /// <see cref="PipelineOnlyBinding.SkippedPipelines"/>, so a PowerFlow is never created half-way or in the wrong app.
    /// </summary>
    public static PipelineOnlyBinding Bind(string content, IReadOnlyList<PipelineOnlyApp> candidates)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(content))
            stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
            || Child(root, "Resources") is not YamlMappingNode resources)
            return new PipelineOnlyBinding { Content = content };
        var parameters = Child(root, "ParameterDefinitions") as YamlMappingNode;
        var binding = new PipelineOnlyBinding();

        // 1. The stored Quickbase ids. Quickbase ids are unique in a realm; if the same id was imported twice (an app imported
        //    again), the newest app wins - the same rule the Quickbase-compatible API uses (FindPublicIdByQbTableIdAsync).
        var appByQbId = new Dictionary<string, PipelineOnlyApp>(StringComparer.OrdinalIgnoreCase);
        var tableByQbId = new Dictionary<string, (PipelineOnlyApp App, PipelineOnlyTable Table)>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in candidates.OrderBy(a => a.Id))
        {
            if (!string.IsNullOrWhiteSpace(app.QbAppId))
            {
                if (appByQbId.TryGetValue(app.QbAppId.Trim(), out var older))
                    binding.Notes.Add($"Quickbase app {app.QbAppId.Trim()} was imported more than once ('{older.Name}', '{app.Name}'); the newest, '{app.Name}', was used.");
                appByQbId[app.QbAppId.Trim()] = app;
            }
            foreach (var table in app.Tables.Where(t => !string.IsNullOrWhiteSpace(t.QbTableId)))
                tableByQbId[table.QbTableId!.Trim()] = (app, table);
        }

        // 2. Per pipeline: every Quickbase table and app id it uses must be found, else the whole pipeline is skipped.
        var tableOf = new Dictionary<string, (PipelineOnlyApp App, PipelineOnlyTable Table)>(StringComparer.Ordinal);
        var chosenApps = new Dictionary<string, PipelineOnlyApp>(StringComparer.Ordinal);
        foreach (var (key, node) in resources.Children.ToList())
        {
            if (node is not YamlMappingNode resource || Scalar(resource, "Type") != "QB::Pipeline") continue;
            var name = PipelineName(resource, parameters) ?? (key as YamlScalarNode)?.Value ?? "(no name)";
            var pipelineUses = new Dictionary<string, TableUse>(StringComparer.Ordinal);
            var tables = CollectPipeline(resource, pipelineUses);
            var apps = AppIds(resource).Concat(pipelineUses.Values.Select(u => u.App).OfType<string>())
                .Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal).ToList();

            var missingTables = tables.Where(t => !tableByQbId.ContainsKey(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
            var missingApps = apps.Where(a => !appByQbId.ContainsKey(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();
            // A step that names the table's app: the table must have been imported inside that same app.
            var wrongApp = pipelineUses.Values
                .Where(u => u.App is not null && tableByQbId.TryGetValue(u.Table, out var t) && appByQbId.TryGetValue(u.App, out var a) && a.Id != t.App.Id)
                .Select(u => u.Table).ToList();
            string? reason = null;
            if (tables.Count == 0 && apps.Count == 0)
                reason = "It names no Quickbase app or table id (nothing to match it to).";
            else if (missingApps.Count > 0 || missingTables.Count > 0)
                reason = "Not found among the imported apps: "
                    + string.Join("; ", new[]
                    {
                        missingApps.Count > 0 ? $"Quickbase app id {string.Join(", ", missingApps)}" : null,
                        missingTables.Count > 0 ? $"Quickbase table id {string.Join(", ", missingTables)}" : null,
                    }.OfType<string>())
                    + ". Import that Quickbase application first, then this file again.";
            else if (wrongApp.Count > 0)
                reason = $"Quickbase table id {string.Join(", ", wrongApp)} was imported in a different app than the Quickbase app its step names.";
            Trace?.Invoke($"{name}\ttables={string.Join(",", tables)}\tapps={string.Join(",", apps)}\t{reason ?? "OK"}");
            if (reason is not null)
            {
                resources.Children.Remove(key);
                binding.SkippedPipelines.Add((name, reason));
                foreach (var t in missingTables) if (!binding.UnmatchedTables.Contains(t)) binding.UnmatchedTables.Add(t);
                foreach (var a in missingApps) if (!binding.UnmatchedApps.Contains(a)) binding.UnmatchedApps.Add(a);
                continue;
            }
            foreach (var table in tables)
            {
                var found = tableByQbId[table];
                if (tableOf.TryAdd(table, found))
                    binding.Matches.Add(new PipelineOnlyTableMatch(found.App.QbAppId, table, found.App.PublicId, found.App.Name, found.Table.Id, found.Table.Name));
            }
            foreach (var app in apps) chosenApps[app] = appByQbId[app];
        }

        var appRefs = new Dictionary<long, string>();
        foreach (var match in binding.Matches)
        {
            var app = candidates.First(a => a.PublicId == match.AppPublicId);
            if (!appRefs.ContainsKey(app.Id)) appRefs[app.Id] = $"$App_PowerBase_{appRefs.Count + 1}";
        }

        // Apps a Quickbase app id was matched to keep their ref even when none of their tables was (DataSource::App only).
        foreach (var (_, app) in chosenApps)
        {
            if (!appRefs.ContainsKey(app.Id)) appRefs[app.Id] = $"$App_PowerBase_{appRefs.Count + 1}";
        }

        // 3. Rewrite the pipelines' data sources into !Refs, then add the matched apps.
        var usedFields = new Dictionary<(long Table, int Fid), bool>();
        foreach (var (_, node) in resources.Children)
            if (node is YamlMappingNode resource && Scalar(resource, "Type") == "QB::Pipeline")
                RewriteDataSources(resource, tableOf, appByQbId, appRefs, binding);

        // Only the tables the rewritten pipelines name (or that a Quickbase table was matched to) go into the file: adding every
        // table of every matched app made the file 2-3 times larger and every later read of it that much slower.
        var named = new HashSet<long>();
        foreach (var (_, node) in resources.Children)
            if (node is YamlMappingNode resource && Scalar(resource, "Type") == "QB::Pipeline")
                foreach (var scalar in AllScalars(resource))
                    foreach (Match m in PbTableRef.Matches(scalar))
                        if (long.TryParse(m.Groups[1].Value, out var id)) named.Add(id);

        foreach (var app in candidates.Where(a => appRefs.ContainsKey(a.Id)))
        {
            var appRef = appRefs[app.Id];
            var qbTables = tableOf.Where(kv => kv.Value.App.Id == app.Id)
                .GroupBy(kv => kv.Value.Table.Id).ToDictionary(g => g.Key, g => string.Join(",", g.Select(kv => kv.Key)));
            var qbApps = string.Join(",", appByQbId.Where(kv => kv.Value.Id == app.Id).Select(kv => kv.Key));
            resources.Children[new YamlScalarNode(appRef)] = AppNode(app with { Tables = app.Tables.Where(t => named.Contains(t.Id) || qbTables.ContainsKey(t.Id)).ToList() }, qbApps, qbTables);
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
            UnmatchedApps = binding.UnmatchedApps, Notes = binding.Notes, SkippedPipelines = binding.SkippedPipelines,
        };
    }

    /// <summary>The pipeline's name (plain, or a <c>!Var</c> parameter of the file).</summary>
    private static string? PipelineName(YamlMappingNode pipeline, YamlMappingNode? parameters)
    {
        if (Child(pipeline, "Properties") is not YamlMappingNode props) return null;
        return Child(props, "Name") switch
        {
            YamlScalarNode { Value: { } name } => name,
            YamlMappingNode var when Scalar(var, "Name") is { } param && parameters is not null
                && Child(parameters, param) is YamlMappingNode def && Scalar(def, "Value") is { } value => value,
            _ => null,
        };
    }

    /// <summary>Every Quickbase app id a step's <c>DataSource::App</c> names.</summary>
    private static IEnumerable<string> AppIds(YamlMappingNode pipeline) =>
        Mappings(pipeline)
            .Where(m => Scalar(m, "Type") == "QB::PipelineStep::DataSource::App" && Child(m, "Properties") is YamlMappingNode)
            .Select(m => Scalar((YamlMappingNode)Child(m, "Properties")!, "App"))
            .OfType<string>();


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
                foreach (var (qbTable, fids) in QuickbaseApiUrlMap.Evidence(QuickbaseApiUrlMap.AbsoluteQuickbaseUrl(Scalar(inputs, "Url")), Scalar(inputs, "Body")))
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

    private static readonly Regex PbTableRef = new(@"\$Table_PB_(\d+)", RegexOptions.Compiled);

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
