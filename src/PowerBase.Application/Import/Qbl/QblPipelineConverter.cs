using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Exceptions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Converts the <c>QB::Pipeline</c> resources of a QBL document into <see cref="PblPipeline"/>s. Pipelines are top-level resources
/// next to the applications; their steps read tables and fields of those applications through
/// <c>InputsMeta → PipelineStep_DataSources</c> (<c>!Ref {App, Table, Field}</c>), field keys in <c>Inputs</c>
/// (<c>Employee_Name</c> → label "Employee Name") and Jinja tokens (<c>{{aa.name}}</c>, <c>{{aa.id}}</c> → Record ID#).
///
/// Only step shapes confirmed against a real export are converted (<see cref="QblPipelineStepCatalog"/>). Everything else is
/// skipped and reported, and so is whatever depends on it:
///   • a step that is not supported, or sits inside one, is skipped;
///   • a step whose target (Update/Delete/Loop/bulk row) is skipped, missing or not usable as a target is skipped too, since
///     PowerFlows cannot save it without one;
///   • a value that refers to a skipped or unreachable step, or uses a token PowerFlows has no equivalent for, is cleared and its
///     step saved as a draft, which keeps the PowerFlow from being turned on until it is fixed.
/// Nothing here touches the database; ids are resolved later by <c>PblPipelinesImporter</c>. Never throws: a pipeline that cannot
/// be read is returned with <see cref="PblPipeline.IsUnreadable"/> set.
/// </summary>
public static class QblPipelineConverter
{
    public const string PIPELINE_TYPE = "QB::Pipeline";
    public const string CHANNEL_ACCOUNT_TYPE = "QB::PipelineChannelAccount";
    public const string UNSUPPORTED_STEP_MESSAGE = "Unsupported – This step is not available in the current PowerFlows application.";
    public const string LINK_IGNORED_MESSAGE = "Link configuration is not supported in the current PowerFlows application and will be ignored.";

    private static readonly HashSet<string> KnownStepProperties = new(StringComparer.Ordinal)
    {
        "Id", "Type", "Name", "SubType", "PipelineChannelAccount", "Channel", "Inputs", "Target", "Filters", "Note", "Link", "Mode",
    };

    /// <summary>The user details a token can read after a user field (<c>{{aa.owner.email}}</c>).</summary>
    private static readonly HashSet<string> UserDetails = new(StringComparer.Ordinal) { "id", "name", "first_name", "last_name", "email", "screen_name" };

    /// <summary>The parts a token can read after a file field (<c>{{ad.file_field.file_transfer_handle}}</c>).</summary>
    private static readonly HashSet<string> FileDetails = new(StringComparer.Ordinal) { "file_transfer_handle", "browser_url", "file_name", "file_size" };

    private static readonly HashSet<string> UserFieldTypes = new(StringComparer.Ordinal)
    {
        "QB::Field::User", "QB::Field::User::Formula", "QB::Field::RecordOwner", "QB::Field::LastModifiedBy",
    };

    /// <summary>What a Handle Errors "On Error" branch can read about the failed step (<c>{{ERROR.error_message}}</c>).</summary>
    private static readonly HashSet<string> ErrorDetails = new(StringComparer.Ordinal) { "reference_id", "channel", "step", "name", "error_message" };

    /// <summary>What a called PowerFlow can read about its caller (<c>{{aa.calling_pipeline.name}}</c>).</summary>
    private static readonly HashSet<string> CallingPipelineDetails = new(StringComparer.Ordinal) { "id", "name", "url", "activity_url", "triggered_at" };

    /// <summary>What later steps can read from an Incoming Request (the editor's <c>buildWebhookResponseFields</c>).</summary>
    private static readonly HashSet<string> WebhookOutputs = new(StringComparer.Ordinal)
    {
        "body", "json", "method", "content_type", "headers", "url_params", "body_params", "origin_ip", "jwt_payload",
    };

    /// <summary>Quickbase keys for the fields every table has, by field type.</summary>
    private static readonly Dictionary<string, string> BuiltInKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "QB::Field::RecordID", ["record_id"] = "QB::Field::RecordID",
        ["created_at"] = "QB::Field::DateCreated", ["date_created"] = "QB::Field::DateCreated",
        ["updated_at"] = "QB::Field::DateModified", ["date_modified"] = "QB::Field::DateModified",
        ["record_owner"] = "QB::Field::RecordOwner", ["last_modified_by"] = "QB::Field::LastModifiedBy",
    };

    private static readonly HashSet<string> KnownPipelineProperties = new(StringComparer.Ordinal)
    {
        "Name", "Description", "Tags", "Owner", "Crontab", "CrontabTimezone",
    };

    /// <summary>Step references PowerFlows itself uses in tokens; a Quickbase step id equal to one of these gets a new reference.</summary>
    private static readonly HashSet<string> ReservedRefIds = new(StringComparer.OrdinalIgnoreCase) { "trigger", "steps", "variables", "ERROR" };

    /// <summary>The pipelines of the file. <paramref name="qbl"/> is the parsed document (applications, tables, fields,
    /// parameters); <paramref name="content"/> is the file itself, from which the pipeline resources are read again (see
    /// <see cref="ReadPipelineResources"/>).</summary>
    /// <param name="skipStepsWithUnmatchedTable">Pipeline-only import: a step whose Quickbase table matched no existing table
    /// (it is still a bare Quickbase id) is skipped and reported instead of imported without a table.</param>
    /// <summary>The file read as plain YAML, as <see cref="ConvertAll"/> reads it. Done ahead by a caller that wants to read it
    /// while it deserializes the same file (the two reads are independent).</summary>
    public static object? ReadRaw(string content)
    {
        try { return RawDeserializer.Deserialize<object>(content); }
        catch (YamlException)
        {
            var sanitized = QblRawTextSanitizer.TryStripUnsupportedPagesBlock(content);
            if (sanitized is null) throw;
            return RawDeserializer.Deserialize<object>(sanitized);
        }
    }

    public static IReadOnlyList<PblPipeline> ConvertAll(QblDocument qbl, string content, bool skipStepsWithUnmatchedTable = false) =>
        ConvertAll(qbl, content, skipStepsWithUnmatchedTable, raw: null);

    /// <param name="raw">The file already read by <see cref="ReadRaw"/>; null reads it here.</param>
    public static IReadOnlyList<PblPipeline> ConvertAll(QblDocument qbl, string content, bool skipStepsWithUnmatchedTable, Func<object?>? raw)
    {
        var pipelineRefs = qbl.Resources.Where(r => r.Value.Type == PIPELINE_TYPE).Select(r => r.Key).ToList();
        if (pipelineRefs.Count == 0)
            return [];

        Dictionary<string, QblResourceNode> resources;
        try
        {
            resources = ReadPipelineResources(raw is null ? ReadRaw(content) : raw(), qbl.ParameterDefinitions);
        }
        catch (Exception ex)
        {
            return pipelineRefs.Select(r => Unreadable(r, qbl.Resources[r], ex.Message)).ToList();
        }

        var accountChannels = resources
            .Where(r => r.Value.Type == CHANNEL_ACCOUNT_TYPE)
            .ToDictionary(r => r.Key, r => Scalar(r.Value.Properties, "Channel") ?? "", StringComparer.Ordinal);
        // The account's name in Quickbase ("ysl-dev"); an HTTP Make Request keeps it as the name of the account to connect.
        var accountAliases = resources
            .Where(r => r.Value.Type == CHANNEL_ACCOUNT_TYPE && !string.IsNullOrWhiteSpace(Scalar(r.Value.Properties, "Alias")))
            .ToDictionary(r => r.Key, r => Scalar(r.Value.Properties, "Alias")!.Trim(), StringComparer.Ordinal);
        // Each pipeline is read on its own (the index and the lookups are only read), so a file of 1000+ pipelines is read in
        // parallel; the result keeps the file's order.
        var index = FieldIndex.Build(qbl);
        var result = new PblPipeline[pipelineRefs.Count];
        Parallel.For(0, pipelineRefs.Count, i =>
        {
            var pipelineRef = pipelineRefs[i];
            try
            {
                if (!resources.TryGetValue(pipelineRef, out var node))
                    throw new InvalidOperationException("the pipeline was not found when the file was read again");
                result[i] = new PipelineReader(pipelineRef, node, accountChannels, index, accountAliases, skipStepsWithUnmatchedTable).Read();
            }
            catch (Exception ex)
            {
                result[i] = Unreadable(pipelineRef, qbl.Resources[pipelineRef], ex.Message);
            }
        });
        return result;
    }

    private static readonly IDeserializer RawDeserializer = new DeserializerBuilder()
        .WithTagMapping("!Ref", typeof(QblRef))
        .WithTagMapping("!BadRef", typeof(QblBadRef))
        .WithTagMapping("!Var", typeof(QblVarRef))
        .WithNodeDeserializer(new QblTagNodeDeserializer(), s => s.Before<ScalarNodeDeserializer>())
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// The pipeline and channel-account resources of the file, read with the same tags as <see cref="QblSerializer"/> but keeping
    /// every <c>Properties</c> map as written. The shared reader turns any mapping with a <c>Type</c> key into a node, and a
    /// pipeline step's Properties carry one (<c>Type: pipe</c>, <c>loop</c>, <c>branch</c>, <c>stop</c>) — read that way the step's id,
    /// channel, inputs and target are lost. Reading them separately leaves the application import untouched.
    /// </summary>
    private static Dictionary<string, QblResourceNode> ReadPipelineResources(object? raw, IReadOnlyDictionary<string, object?> parameters)
    {

        var result = new Dictionary<string, QblResourceNode>(StringComparer.Ordinal);
        if (raw is IDictionary<object, object> document && document.TryGetValue("Resources", out var resources) && resources is IDictionary<object, object> map)
        {
            foreach (var (key, value) in map)
            {
                if (value is IDictionary<object, object> resource && resource.TryGetValue("Type", out var type)
                    && type?.ToString() is PIPELINE_TYPE or CHANNEL_ACCOUNT_TYPE)
                    result[key?.ToString() ?? string.Empty] = NodeOf(resource, parameters);
            }
        }
        return result;
    }

    private static QblResourceNode NodeOf(IDictionary<object, object> raw, IReadOnlyDictionary<string, object?> parameters)
    {
        IReadOnlyDictionary<string, object?> properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var children = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (rawKey, value) in raw)
        {
            var key = rawKey?.ToString() ?? string.Empty;
            if (key == "Type")
                continue;
            if (key == "Properties")
                properties = Plain(value, parameters) as IReadOnlyDictionary<string, object?> ?? properties;
            else
                children[key] = Nodes(value, parameters);
        }
        return new QblResourceNode { Type = raw.TryGetValue("Type", out var type) ? type?.ToString() ?? string.Empty : string.Empty, Properties = properties, Children = children };
    }

    /// <summary>Steps, InputsMeta and data sources: a mapping with a <c>Type</c> is a node.</summary>
    private static object? Nodes(object? raw, IReadOnlyDictionary<string, object?> parameters) => raw switch
    {
        IDictionary<object, object> dict when dict.ContainsKey("Type") => NodeOf(dict, parameters),
        IDictionary<object, object> dict => dict.ToDictionary(kv => kv.Key?.ToString() ?? string.Empty, kv => Nodes(kv.Value, parameters), StringComparer.Ordinal),
        IList<object> list => list.Select(item => Nodes(item, parameters)).ToList(),
        _ => Plain(raw, parameters),
    };

    /// <summary>Settings as written: maps stay maps, <c>!Var</c> resolves to its parameter's value.</summary>
    private static object? Plain(object? raw, IReadOnlyDictionary<string, object?> parameters) => raw switch
    {
        QblVarRef variable => parameters.TryGetValue(variable.ParameterName, out var value) ? value : null,
        QblRef or QblBadRef => raw,
        IDictionary<object, object> dict => dict.ToDictionary(kv => kv.Key?.ToString() ?? string.Empty, kv => Plain(kv.Value, parameters), StringComparer.Ordinal),
        IList<object> list => list.Select(item => Plain(item, parameters)).ToList(),
        _ => raw,
    };

    /// <summary>The pipelines the import of application number <paramref name="appIndex"/> (ref <paramref name="appRefs"/>[i]) is
    /// responsible for: those created in it, plus — on the first application — those that name no imported application, so they
    /// are still reported.</summary>
    public static IReadOnlyList<PblPipeline> ForApp(IReadOnlyList<PblPipeline> pipelines, IReadOnlyList<string> appRefs, int appIndex)
    {
        if (pipelines.Count == 0 || appIndex < 0 || appIndex >= appRefs.Count)
            return [];
        var appRef = appRefs[appIndex];
        return pipelines
            .Where(p => string.Equals(p.AppRef, appRef, StringComparison.Ordinal)
                        || (appIndex == 0 && (p.AppRef is null || !appRefs.Contains(p.AppRef, StringComparer.Ordinal))))
            .ToList();
    }

    private static PblPipeline Unreadable(string pipelineRef, QblResourceNode node, string reason)
    {
        var name = Scalar(node.Properties, "Name");
        return new PblPipeline
        {
            LogicalRef = pipelineRef,
            Name = string.IsNullOrWhiteSpace(name) ? pipelineRef : name,
            IsUnreadable = true,
            Issues =
            [
                new PblPipelineIssue
                {
                    Code = "PIPELINE_UNREADABLE",
                    Message = $"The pipeline could not be read ({reason}); it was not imported.",
                    ChangesExecution = true,
                },
            ],
        };
    }

    private static string? Scalar(IReadOnlyDictionary<string, object?> properties, string name) =>
        properties.TryGetValue(name, out var value) && value is not (null or QblRef or QblBadRef or IDictionary<string, object?> or IList<object?>)
            ? value.ToString()
            : null;

    private static bool IsTrue(object? value) => value is bool b ? b : bool.TryParse(value?.ToString(), out var parsed) && parsed;

    /// <summary>The comparable form of a field label or key: <c>Employee Name</c>, <c>Employee_Name</c> and <c>employee_name</c>
    /// all become <c>employee_name</c>.</summary>
    internal static string NormalizeKey(string text)
    {
        var chars = text.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var key = Regex.Replace(new string(chars), "_+", "_").Trim('_');
        // Quickbase keys a label that starts with a digit with a leading "n" ("30 Days Post Completion" → n30_days_post_completion).
        return key.Length > 0 && char.IsDigit(key[0]) ? "n" + key : key;
    }

    // ------------------------------------------------------------------------------------------------------------------------

    private sealed record QblFieldInfo(string AppRef, string TableRef, string FieldRef, int? SourceId, string Label, string Type)
    {
        public bool IsBuiltIn => QblFieldTypeMap.SystemFieldTypes.Contains(Type);
        public bool IsAddressPart => QblFieldTypeMap.AddressSubComponentTypes.Contains(Type);
        public bool IsRecordId => Type == "QB::Field::RecordID";
        /// <summary>A value the table calculates (formula, lookup, summary): never written by a step.</summary>
        public bool IsComputed => Type.EndsWith("::Formula", StringComparison.Ordinal) || Type is "QB::Field::Lookup" or "QB::Field::Summary";
    }

    /// <summary>Every field of every application in the file, by table, with what a pipeline needs to point at it.</summary>
    private sealed class FieldIndex
    {
        private readonly Dictionary<(string App, string Table), Dictionary<string, QblFieldInfo>> _fields = new();
        private readonly Dictionary<(string App, string Table), Dictionary<string, string>> _addressOwner = new();
        private readonly Dictionary<(string App, string Table, string Field), string> _partKeys = new();
        private readonly HashSet<string> _apps = new(StringComparer.Ordinal);
        private readonly Dictionary<(string App, string Table), QblFieldInfo> _keyFields = new();
        private readonly Dictionary<string, (string App, string Table)> _tablesByQbId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _appsByQbId = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Apps => _apps;

        /// <summary>The table a Quickbase table id stands for. Only a pipeline-only import knows them (QblPipelineOnlyBinder
        /// writes <c>QbTableIds</c> on the tables it matched); Quickbase application exports carry no table ids.</summary>
        public (string App, string Table)? TableByQbId(string id) => _tablesByQbId.TryGetValue(id, out var t) ? t : null;

        public string? AppByQbId(string? id) => id is not null && _appsByQbId.TryGetValue(id, out var a) ? a : null;

        public static FieldIndex Build(QblDocument qbl)
        {
            var index = new FieldIndex();
            foreach (var (appRef, appNode) in qbl.Resources.Where(r => r.Value.Type == "QB::Application"))
            {
                index._apps.Add(appRef);
                foreach (var qbId in (Scalar(appNode.Properties, "QbAppIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    index._appsByQbId.TryAdd(qbId, appRef);
                foreach (var (tableRef, tableNode) in appNode.ChildMap("Tables"))
                {
                    var fields = new Dictionary<string, QblFieldInfo>(StringComparer.Ordinal);
                    var owners = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (fieldRef, fieldNode) in tableNode.ChildMap("Fields"))
                    {
                        var label = Scalar(fieldNode.Properties, "Label");
                        if (string.IsNullOrWhiteSpace(label))
                            label = (fieldRef.StartsWith("$Field_", StringComparison.Ordinal) ? fieldRef["$Field_".Length..] : fieldRef.TrimStart('$')).Replace('_', ' ');
                        fields[fieldRef] = new QblFieldInfo(appRef, tableRef, fieldRef, fieldNode.SourceId, label, fieldNode.Type);

                        if (fieldNode.Type == "QB::Field::Address")
                            foreach (var (property, key) in QblFieldTypeMap.AddressSubComponentPropertyToKey)
                                if (fieldNode.PropertyRef(property)?["Field"] is { } partRef)
                                {
                                    owners[partRef] = fieldRef;
                                    index._partKeys[(appRef, tableRef, partRef)] = key;
                                }
                    }
                    index._fields[(appRef, tableRef)] = fields;
                    foreach (var qbId in (Scalar(tableNode.Properties, "QbTableIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        index._tablesByQbId.TryAdd(qbId, (appRef, tableRef));
                    index._addressOwner[(appRef, tableRef)] = owners;
                    // Set Key: the field Quickbase looks a record of this table up by (Record ID# when there is none).
                    if (tableNode.PropertyRef("KeyField")?["Field"] is { } keyRef && fields.TryGetValue(keyRef, out var keyField))
                        index._keyFields[(appRef, tableRef)] = keyField;
                }
            }
            return index;
        }

        public bool HasTable(string app, string table) => _fields.ContainsKey((app, table));

        /// <summary>The table's key field (Set Key), or null when its key is Record ID#.</summary>
        public QblFieldInfo? KeyField(string app, string table) =>
            _keyFields.TryGetValue((app, table), out var key) && !key.IsRecordId ? key : null;

        public IReadOnlyCollection<QblFieldInfo> Fields(string app, string table) =>
            _fields.TryGetValue((app, table), out var map) ? map.Values : [];

        public QblFieldInfo? Field(string app, string table, string field) =>
            _fields.TryGetValue((app, table), out var map) && map.TryGetValue(field, out var info) ? info : null;

        /// <summary>The key PowerBase stores an address part under (<c>street1</c>, <c>city</c>, <c>zip</c>…).</summary>
        public string? PartKey(QblFieldInfo part) =>
            _partKeys.TryGetValue((part.AppRef, part.TableRef, part.FieldRef), out var key) ? key : null;

        /// <summary>The composite Address field an address part (City, State…) belongs to.</summary>
        public QblFieldInfo? AddressOf(QblFieldInfo part) =>
            _addressOwner.TryGetValue((part.AppRef, part.TableRef), out var owners) && owners.TryGetValue(part.FieldRef, out var composite)
                ? Field(part.AppRef, part.TableRef, composite)
                : null;
    }

    /// <summary>One step of the source pipeline, as read before anything is converted.</summary>
    private sealed class SourceStep
    {
        public required QblResourceNode Node { get; init; }
        public required string Location { get; init; }
        public SourceStep? Parent { get; init; }
        public required int Order { get; init; }
        public string? Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public QblPipelineStepCatalog.Entry? Entry { get; init; }
        public string? Channel { get; init; }
        public string? Resource { get; init; }
        public string? Selector { get; init; }
        public string? TargetId { get; init; }
        public string RefId { get; set; } = string.Empty;
        public bool Removed { get; set; }
        public List<SourceStep> Steps { get; } = [];
        public List<SourceStep> Then { get; } = [];
        public List<SourceStep> Else { get; } = [];
        public List<SourceStep> Try { get; } = [];
        public List<SourceStep> OnSuccess { get; } = [];
        public List<SourceStep> OnError { get; } = [];

        /// <summary>The branch of <see cref="Parent"/> the step is in (<c>Steps</c>, <c>Then</c>, <c>Else</c>, <c>Try</c>, <c>OnSuccess</c>, <c>OnError</c>).</summary>
        public string? Branch { get; init; }
        public Dictionary<string, QblResourceNode> DataSources { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, object?> Meta { get; } = new(StringComparer.Ordinal);

        /// <summary>The table the step reads or writes (for Update/Delete/bulk rows: its target's).</summary>
        public (string App, string Table)? Table { get; set; }
        public string? AppRef { get; set; }

        public IEnumerable<SourceStep> Nested => Steps.Concat(Then).Concat(Else).Concat(Try).Concat(OnSuccess).Concat(OnError);

        /// <summary>Inside the On Error branch of a Handle Errors step, where the failed step's details ({{ERROR.…}}) exist.</summary>
        public bool IsInErrorBranch()
        {
            for (var current = this; current.Parent is not null; current = current.Parent)
                if (current.Branch == "OnError") return true;
            return false;
        }

        /// <summary>The nearest enclosing Loop that iterates over step <paramref name="targetId"/>.</summary>
        public SourceStep? LoopOver(string targetId)
        {
            for (var current = Parent; current is not null; current = current.Parent)
                if (current.Subtype == "for-each" && current.TargetId == targetId) return current;
            return null;
        }
        public string Label => Id ?? $"(no id) at {Location}";
        public string Subtype => Entry?.Subtype ?? string.Empty;

        public IEnumerable<SourceStep> Ancestors()
        {
            var path = new List<SourceStep>();
            for (var current = Parent; current is not null; current = current.Parent)
                path.Add(current);
            path.Reverse();
            return path;
        }

        public bool IsInside(SourceStep container)
        {
            for (var current = Parent; current is not null; current = current.Parent)
                if (ReferenceEquals(current, container)) return true;
            return false;
        }
    }

    private sealed class PipelineReader(string pipelineRef, QblResourceNode node, IReadOnlyDictionary<string, string> accountChannels, FieldIndex index,
        IReadOnlyDictionary<string, string> accountAliases, bool skipStepsWithUnmatchedTable = false)
    {
        private static readonly Regex TokenPattern = new(@"\{\{(.*?)\}\}|\{%.*?%\}", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex PathToken = new(@"^\s*([A-Za-z][A-Za-z0-9_]*)((?:\.\$?[A-Za-z0-9_]+)+)\s*$", RegexOptions.Compiled); // powerflowextraRd: "$" allowed ({{a.$prev.name}})
        private static readonly Regex ValidRefId = new(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private readonly PblPipeline _pipeline = new() { LogicalRef = pipelineRef };
        private readonly List<SourceStep> _all = [];
        private readonly List<SourceStep> _roots = [];
        private readonly Dictionary<string, SourceStep> _byId = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, string> _accountChannels = accountChannels;
        private readonly IReadOnlyDictionary<string, string> _accountAliases = accountAliases;

        public PblPipeline Read()
        {
            var name = Scalar(node.Properties, "Name");
            _pipeline.Name = string.IsNullOrWhiteSpace(name) ? "New pipeline" : name.Trim();
            _pipeline.Description = Scalar(node.Properties, "Description");
            _pipeline.Owner = Scalar(node.Properties, "Owner");
            _pipeline.Crontab = Scalar(node.Properties, "Crontab");
            _pipeline.CrontabTimezone = Scalar(node.Properties, "CrontabTimezone");
            NotePipelineProperties();

            Walk(StepList(node, "Steps"), null, "", _roots);
            _pipeline.SourceStepCount = _all.Count;
            AssignRefIds();
            SkipUnsupported();
            if (skipStepsWithUnmatchedTable) SkipUnmatchedTables();
            ResolveTables();
            SkipBrokenDependencies();
            _pipeline.AppRef = PickApp();
            _pipeline.Steps = Build(_roots);
            // A PowerFlow whose steps name no table data source (only a Quickbase API call, rewritten to an existing table) belongs
            // to the app of that table.
            _pipeline.AppRef ??= AllSteps(_pipeline.Steps).SelectMany(s => TableValues(s.Config)).Select(t => t.AppRef).FirstOrDefault(a => !string.IsNullOrEmpty(a));
            RequireValidStart();
            return _pipeline;
        }

        // ---- reading -----------------------------------------------------------------------------------------------------

        private static List<QblResourceNode> StepList(QblResourceNode owner, string key) =>
            owner.Children.TryGetValue(key, out var raw) && raw is IEnumerable<object?> items
                ? items.OfType<QblResourceNode>().ToList()
                : [];

        private void Walk(List<QblResourceNode> nodes, SourceStep? parent, string prefix, List<SourceStep> into, string? branch = null)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var stepNode = nodes[i];
                var channel = stepNode.Properties.TryGetValue("Channel", out var rawChannel) ? rawChannel as IReadOnlyDictionary<string, object?> : null;
                var target = stepNode.Properties.TryGetValue("Target", out var rawTarget) ? rawTarget as IReadOnlyDictionary<string, object?> : null;
                var channelId = channel is null ? null : Scalar(channel, "Id");
                var resource = channel is null ? null : Scalar(channel, "Resource");
                var selector = channel is null ? null : Scalar(channel, "Selector");
                var name = Scalar(stepNode.Properties, "Name");

                var step = new SourceStep
                {
                    Node = stepNode,
                    Location = prefix + (i + 1),
                    Parent = parent,
                    Branch = branch,
                    Order = _all.Count,
                    Id = Scalar(stepNode.Properties, "Id"),
                    Name = string.IsNullOrWhiteSpace(name) ? stepNode.Type : name,
                    Entry = QblPipelineStepCatalog.Find(stepNode.Type, channelId, resource, selector),
                    Channel = channelId,
                    Resource = resource,
                    Selector = selector,
                    TargetId = target is null ? null : Scalar(target, "StepId"),
                };
                _all.Add(step);
                into.Add(step);
                if (step.Id is not null)
                    _byId.TryAdd(step.Id, step);

                foreach (var (_, metaNode) in stepNode.ChildMap("InputsMeta"))
                {
                    foreach (var (key, value) in metaNode.Properties)
                        step.Meta[key] = value;
                    foreach (var (_, source) in metaNode.ChildMap("PipelineStep_DataSources"))
                        if (Scalar(source.Properties, "Name") is { } sourceName)
                            step.DataSources[sourceName] = source;
                }

                Walk(StepList(stepNode, "Steps"), step, $"{step.Location}.Steps.", step.Steps, "Steps");
                Walk(StepList(stepNode, "Then"), step, $"{step.Location}.Then.", step.Then, "Then");
                Walk(StepList(stepNode, "Else"), step, $"{step.Location}.Else.", step.Else, "Else");
                Walk(StepList(stepNode, "Try"), step, $"{step.Location}.Try.", step.Try, "Try");
                Walk(StepList(stepNode, "OnSuccess"), step, $"{step.Location}.OnSuccess.", step.OnSuccess, "OnSuccess");
                Walk(StepList(stepNode, "OnError"), step, $"{step.Location}.OnError.", step.OnError, "OnError");
            }
        }

        private void AssignRefIds()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var sourceIds = new HashSet<string>(_all.Where(s => s.Id is not null).Select(s => s.Id!), StringComparer.Ordinal);
            var next = 1;
            foreach (var step in _all)
            {
                // The editor names every step "ref_…" (its field pickers write {{steps.ref_x.…}}), so a Quickbase id "aa" is
                // kept as "ref_aa"; ids without a letter-first form get a generated reference.
                if (step.Id is { } id && ValidRefId.IsMatch(id) && !ReservedRefIds.Contains(id) && !id.StartsWith("fid_", StringComparison.OrdinalIgnoreCase)
                    && !sourceIds.Contains($"ref_{id}") && used.Add($"ref_{id}"))
                {
                    step.RefId = $"ref_{id}";
                    continue;
                }
                string generated;
                do generated = $"ref_{next++}"; while (sourceIds.Contains(generated) || !used.Add(generated));
                step.RefId = generated;
                if (step.Id is not null)
                    Note(step, "PIPELINE_STEP_ID_CHANGED", $"Step id '{step.Id}' cannot be used in PowerFlows; the step is referred to as '{generated}'.", changesExecution: false);
            }
        }

        private void NotePipelineProperties()
        {
            if (node.Properties.TryGetValue("Tags", out var tags) && tags is IList<object?> { Count: > 0 })
                _pipeline.Issues.Add(new PblPipelineIssue { Code = "PIPELINE_TAGS_NOT_IMPORTED", Message = "Pipeline tags are not imported; PowerFlows has no tags." });
            foreach (var key in node.Properties.Keys.Where(k => !KnownPipelineProperties.Contains(k)))
                _pipeline.Issues.Add(new PblPipelineIssue { Code = "PIPELINE_SETTING_NOT_IMPORTED", Message = $"Pipeline setting '{key}' is not imported.", ChangesExecution = true });
        }

        // ---- skipping ----------------------------------------------------------------------------------------------------

        private void SkipUnsupported()
        {
            foreach (var step in _all.Where(s => s.Entry is null && !s.Removed))
            {
                var source = step.Selector is null
                    ? step.Node.Type
                    : $"{step.Node.Type}: {step.Channel}/{step.Resource}/{step.Selector}";
                Remove(step, "PIPELINE_STEP_UNSUPPORTED", $"{UNSUPPORTED_STEP_MESSAGE} (Quickbase step '{step.Name}', {source}.)");
            }
        }

        private void Remove(SourceStep step, string code, string message)
        {
            if (step.Removed) return;
            step.Removed = true;
            _pipeline.Issues.Add(new PblPipelineIssue
            {
                Code = code,
                Message = message,
                StepId = step.Label,
                StepName = step.Name,
                IsSkippedStep = true,
                ChangesExecution = true,
            });
            foreach (var nested in step.Nested)
                Remove(nested, "PIPELINE_STEP_PARENT_SKIPPED", $"Skipped because the step that contains it ('{step.Name}', {step.Label}) is not imported.");
        }

        /// <summary>Pipeline-only import: a table data source that is still a bare Quickbase table id (bwcrtu9k5) matched no table of
        /// the user's apps — its app was not imported. Such a step is skipped (and reported), never pointed at another table.</summary>
        private void SkipUnmatchedTables()
        {
            foreach (var step in _all.Where(s => !s.Removed).ToList())
                foreach (var (name, source) in step.DataSources)
                    if (source.Type == "QB::PipelineStep::DataSource::Table"
                        && source.Properties.TryGetValue("Table", out var value) && value is string qbTable && !string.IsNullOrWhiteSpace(qbTable))
                    {
                        Remove(step, "PIPELINE_STEP_TABLE_NOT_MATCHED",
                            $"Skipped because its Quickbase table '{qbTable}' ({name}) matches no table of your apps (its Quickbase app was not imported).");
                        break;
                    }
        }

        private void ResolveTables()
        {
            foreach (var step in _all.Where(s => !s.Removed))
            {
                if (TableOf(step.DataSources, "table") is { } table)
                    step.Table = table;
                step.AppRef = AppOf(step.DataSources, "app") ?? step.Table?.App;
            }
        }

        /// <summary>Rule C: what PowerFlows cannot save without — a target step that exists, comes first, is reachable and is of the
        /// kind the step needs. Repeats until nothing more is skipped, since skipping a step can break the ones that point at it.</summary>
        private void SkipBrokenDependencies()
        {
            bool changed;
            do
            {
                changed = false;
                foreach (var step in _all.Where(s => !s.Removed))
                {
                    if (step.Subtype == "stop" && step.Parent is null)
                    {
                        Remove(step, "PIPELINE_STEP_PLACEMENT", "Skipped because PowerFlows only allows Stop inside a Condition or Loop.");
                        changed = true;
                        continue;
                    }

                    if (!NeedsTarget(step.Subtype))
                        continue;

                    var problem = TargetProblem(step);
                    if (problem is not null)
                    {
                        Remove(step, problem.Value.Code, problem.Value.Message);
                        changed = true;
                    }
                    else if (step.Subtype is "update-record" or "delete-record" or "add-bulk-upsert-row" or "commit-upsert" or "upload-file")
                    {
                        var target = _byId[step.TargetId!];
                        step.Table ??= target.Table;
                        step.AppRef ??= target.AppRef;
                    }
                }
            } while (changed);
        }

        private static bool NeedsTarget(string subtype) =>
            subtype is "update-record" or "delete-record" or "add-bulk-upsert-row" or "commit-upsert" or "for-each" or "upload-file";

        private (string Code, string Message)? TargetProblem(SourceStep step)
        {
            if (step.TargetId is null || !_byId.TryGetValue(step.TargetId, out var target))
                return ("PIPELINE_STEP_TARGET_MISSING", $"Skipped because its target step '{step.TargetId}' is not in the pipeline.");
            if (target.Removed)
                return ("PIPELINE_STEP_DEPENDENCY_SKIPPED", $"Skipped because it depends on step '{target.Name}' ({target.Label}), which was not imported.");
            if (target.Order >= step.Order)
                return ("PIPELINE_STEP_TARGET_INVALID", $"Skipped because its target step '{target.Name}' ({target.Label}) does not come before it.");

            var usable = step.Subtype switch
            {
                "update-record" or "delete-record" or "upload-file" => target.Subtype is "create-record" or "update-record" or "look-up-record"
                                                      || (target.Subtype == "new-event" && target.Parent is null && target.Order == FirstRootOrder())
                                                      || (step.Subtype is "update-record" or "delete-record" && LoopItemTarget(step) is not null),
                "add-bulk-upsert-row" or "commit-upsert" => target.Subtype == "prepare-bulk-upsert",
                "for-each" => target.Entry?.Type == "query" || target.Subtype is "new-bulk-event" or "search-records" or "make-request",
                _ => false,
            };
            if (!usable)
                return ("PIPELINE_STEP_TARGET_INVALID", $"Skipped because PowerFlows cannot use step '{target.Name}' ({target.Label}) as its target.");
            // The target is written as a token ({{steps.x.…}}), which PowerFlows only accepts for a reachable step.
            if (step.Subtype is "update-record" or "delete-record" && !IsReachable(target, step))
                return ("PIPELINE_STEP_TARGET_INVALID", $"Skipped because its target step '{target.Name}' ({target.Label}) is inside a branch or loop it cannot see.");
            return null;
        }

        private int FirstRootOrder() => _roots.FirstOrDefault(r => !r.Removed)?.Order ?? -1;

        /// <summary>Quickbase's "the record of this loop": a step inside a Loop that targets the step the loop iterates. PowerFlows
        /// targets the Loop itself ({{steps.loop.item.RecordPublicId}}), for the loops its editor offers (over Search Records or
        /// On New Bulk Event).</summary>
        private SourceStep? LoopItemTarget(SourceStep step)
        {
            if (step.TargetId is null || !_byId.TryGetValue(step.TargetId, out var looped) || looped.Subtype is not ("search-records" or "new-bulk-event"))
                return null;
            var loop = step.LoopOver(step.TargetId);
            return loop is not null && !loop.Removed && loop.RefId.Length > 0 ? loop : null;
        }

        /// <summary>The PowerFlows rule for reading another step's output: it comes first, every container it is in also contains
        /// the reader, and a loop's own output is only visible inside the loop.</summary>
        private static bool IsReachable(SourceStep target, SourceStep reader)
        {
            if (target.Order >= reader.Order) return false;
            var targetPath = target.Ancestors().ToList();
            var readerPath = reader.Ancestors().ToList();
            if (targetPath.Count > readerPath.Count) return false;
            for (var i = 0; i < targetPath.Count; i++)
                if (!ReferenceEquals(targetPath[i], readerPath[i])) return false;
            return target.Entry?.Type != "loop" || reader.IsInside(target);
        }

        private static IEnumerable<PblPipelineStep> AllSteps(IEnumerable<PblPipelineStep> steps) => steps.SelectMany(s =>
            new[] { s }.Concat(AllSteps(s.Children)).Concat(AllSteps(s.ElseChildren)).Concat(AllSteps(s.SuccessChildren)).Concat(AllSteps(s.ErrorChildren)));

        private static IEnumerable<PblPipelineTableValue> TableValues(object? value) => value switch
        {
            PblPipelineTableValue t => [t],
            PblPipelineTextValue t => t.Parts.SelectMany(TableValues),
            IDictionary<string, object?> d => d.Values.SelectMany(TableValues),
            string => [],
            System.Collections.IEnumerable e => e.Cast<object?>().SelectMany(TableValues),
            _ => [],
        };

        private string? PickApp()
        {
            var steps = _all.Where(s => !s.Removed).ToList();
            var app = steps.FirstOrDefault(s => s.Entry?.Type == "trigger")?.AppRef ?? steps.Select(s => s.AppRef).FirstOrDefault(a => a is not null);
            if (app is null && index.Apps.Count == 1)
                app = index.Apps.First();
            return app;
        }

        private void RequireValidStart()
        {
            var first = _pipeline.Steps.FirstOrDefault();
            if (first is null || first.Type == "trigger" || first.Subtype is "search-records" or "look-up-record" or "copy-records" or "prepare-bulk-upsert"
                    or "pause" or "import-with-csv" or "handle-errors" or "send-email-outlook" or "make-request"
                    // As in Quickbase, these actions can start a PowerFlow run by hand or on a schedule.
                    or "create-record" or "update-record" or "delete-record" or "upload-file" or "call-another-pipeline")
                return;

            _pipeline.Issues.Add(new PblPipelineIssue
            {
                Code = "PIPELINE_NO_VALID_START",
                Message = $"The PowerFlow was created without steps: PowerFlows must start with a trigger, Search Records, Look Up a Record, Copy Records, Make Request, Import with CSV, Handle Errors, Prepare Bulk Record Upsert, Pause, Send an Email, Create Record or Call Another PowerFlow, and the first imported step is '{first.Name}'. Add the steps by hand.",
                ChangesExecution = true,
            });
            foreach (var step in Flatten(_pipeline.Steps))
                _pipeline.Issues.Add(new PblPipelineIssue
                {
                    Code = "PIPELINE_STEP_NOT_SAVED",
                    Message = "Not saved because the PowerFlow has no valid first step.",
                    StepId = step.SourceLabel,
                    StepName = step.Name,
                    IsSkippedStep = true,
                    ChangesExecution = true,
                });
            _pipeline.Steps = [];
        }

        private static IEnumerable<PblPipelineStep> Flatten(IEnumerable<PblPipelineStep> steps) =>
            steps.SelectMany(s => new[] { s }.Concat(Flatten(s.Children)).Concat(Flatten(s.ElseChildren))
                .Concat(Flatten(s.SuccessChildren)).Concat(Flatten(s.ErrorChildren)));

        // ---- building ----------------------------------------------------------------------------------------------------

        private List<PblPipelineStep> Build(List<SourceStep> steps) =>
            steps.Where(s => !s.Removed).Select(BuildStep).ToList();

        private PblPipelineStep BuildStep(SourceStep source)
        {
            var step = new PblPipelineStep
            {
                RefId = source.RefId,
                SourceId = source.Id,
                Location = source.Location,
                Name = source.Name,
                Notes = NoteWithoutQuickbaseUrls(source),
                Type = source.Entry!.Type,
                Subtype = source.Entry.Subtype,
                Config = QblPipelineStepCatalog.DefaultConfig(source.Entry.Subtype),
            };
            step.Config["stepLabel"] = source.Name;

            var context = new StepContext(this, source, step);
            context.NoteUnknownProperties();
            switch (step.Subtype)
            {
                case "new-event" or "new-bulk-event": context.NewEvent(); break;
                case "webhook": context.IncomingRequest(); break;
                case "make-request": context.MakeRequest(); break;
                case "search-records": context.SearchRecords(); break;
                case "look-up-record": context.LookUpRecord(); break;
                case "create-record": context.CreateRecord(); break;
                case "update-record": context.UpdateRecord(); break;
                case "delete-record": context.DeleteRecord(); break;
                case "prepare-bulk-upsert": context.PrepareBulkUpsert(); break;
                case "add-bulk-upsert-row": context.AddBulkUpsertRow(); break;
                case "commit-upsert": context.CommitUpsert(); break;
                case "copy-records": context.CopyRecords(); break;
                case "pause": context.Pause(); break;
                case "if-else": context.Condition(); break;
                case "for-each": context.Loop(); break;
                case "stop": break;
                case "handle-errors": context.HandleErrors(); break;
                case "upload-file": context.UploadFile(); break;
                case "import-with-csv": context.ImportWithCsv(); break;
                case "send-email-outlook": context.SendEmail(); break;
                case "pipeline-called": context.PipelineCalled(); break;
                case "call-another-pipeline": context.CallAnotherPipeline(); break;
            }
            context.NoteUnusedSettings();
            RemoveQuickbaseUrls(source, step);

            step.Children = Build(step.Subtype switch { "for-each" => source.Steps, "handle-errors" => source.Try, _ => source.Then });
            step.ElseChildren = Build(source.Else);
            step.SuccessChildren = Build(source.OnSuccess);
            step.ErrorChildren = Build(source.OnError);
            return step;
        }

        /// <summary>The step's note with every Quickbase address rewritten to PowerBase (or removed), like any other value.</summary>
        private string? NoteWithoutQuickbaseUrls(SourceStep source)
        {
            var note = Scalar(source.Node.Properties, "Note");
            if (!QuickbaseUrlRewriter.ContainsQuickbaseUrl(note)) return note;
            // A note is free text with no ids to resolve: the Quickbase address is taken out and named in the report.
            var removed = QuickbaseUrlRewriter.UrlPattern.Matches(note!).Select(m => m.Value).ToList();
            Note(source, "PIPELINE_QUICKBASE_URL_REMOVED", $"Step note: the Quickbase address(es) {string.Join(", ", removed)} were taken out of the note.", changesExecution: false);
            return QuickbaseUrlRewriter.UrlPattern.Replace(note!, string.Empty).Trim();
        }

        /// <summary>Last guard: no imported step keeps a Quickbase URL. Values the rewriter did not see (settings copied as is) are
        /// emptied and reported, and the step stays a draft.</summary>
        private void RemoveQuickbaseUrls(SourceStep source, PblPipelineStep step)
        {
            object? Clean(object? value, string path)
            {
                switch (value)
                {
                    case string s when QuickbaseUrlRewriter.ContainsQuickbaseUrl(s):
                        return Removed(s, path);
                    case PblPipelineTextValue t when t.Parts.Any(p => p is string s && QuickbaseUrlRewriter.ContainsQuickbaseUrl(s)):
                        return Removed(string.Concat(t.Parts.OfType<string>()), path);
                    case Dictionary<string, object?> d:
                        foreach (var k in d.Keys.ToList()) d[k] = Clean(d[k], path + "." + k);
                        return d;
                    case List<object?> l:
                        for (var i = 0; i < l.Count; i++) l[i] = Clean(l[i], $"{path}[{i}]");
                        return l;
                    default:
                        return value;
                }
            }
            object Removed(string text, string path)
            {
                step.IsComplete = false;
                Note(source, "PIPELINE_QUICKBASE_URL_REMOVED",
                    $"'{path}' held a Quickbase address ({QuickbaseUrlRewriter.UrlPattern.Match(text).Value}) and was emptied: PowerFlows never call or link to Quickbase. Set the PowerBase value before turning the PowerFlow on.", changesExecution: true);
                return string.Empty;
            }
            foreach (var key in step.Config.Keys.ToList())
                step.Config[key] = Clean(step.Config[key], key);
        }

        private void Note(SourceStep step, string code, string message, bool changesExecution) =>
            _pipeline.Issues.Add(new PblPipelineIssue { Code = code, Message = message, StepId = step.Label, StepName = step.Name, ChangesExecution = changesExecution });

        private (string App, string Table)? TableOf(IReadOnlyDictionary<string, QblResourceNode> sources, string name)
        {
            if (!sources.TryGetValue(name, out var source) || source.PropertyRef("Table") is not { } table)
                return null;
            var app = table["App"];
            var tableRef = table["Table"];
            return app is null || tableRef is null ? null : (app, tableRef);
        }

        private static string? AppOf(IReadOnlyDictionary<string, QblResourceNode> sources, string name) =>
            sources.TryGetValue(name, out var source) ? source.PropertyRef("App")?["App"] : null;

        /// <summary>Everything about converting one step: what it consumed (so leftovers can be reported) and how it fails.</summary>
        private sealed class StepContext(PipelineReader reader, SourceStep source, PblPipelineStep step)
        {
            private readonly HashSet<string> _usedInputs = new(StringComparer.Ordinal);
            private readonly HashSet<string> _usedMeta = new(StringComparer.Ordinal);
            private readonly HashSet<string> _usedSources = new(StringComparer.Ordinal);
            private bool _addressNoted;

            private Dictionary<string, object?> Config => step.Config;

            private IReadOnlyDictionary<string, object?> Inputs =>
                source.Node.Properties.TryGetValue("Inputs", out var raw) && raw is IReadOnlyDictionary<string, object?> inputs
                    ? inputs
                    : new Dictionary<string, object?>();

            // -- issues

            private void Incomplete(string code, string message)
            {
                step.IsComplete = false;
                reader.Note(source, code, message, changesExecution: true);
            }

            public void NoteUnknownProperties()
            {
                foreach (var key in source.Node.Properties.Keys.Where(k => !KnownStepProperties.Contains(k)))
                    reader.Note(source, "PIPELINE_SETTING_NOT_IMPORTED", $"Setting '{key}' is not imported.", changesExecution: true);
                if (source.Node.Properties.ContainsKey("Link"))
                    reader.Note(source, "PIPELINE_LINK_IGNORED", LINK_IGNORED_MESSAGE, changesExecution: false);
                if (!source.Node.Properties.TryGetValue("PipelineChannelAccount", out var account) || account is not QblRef accountRef)
                    return;
                var channel = accountRef["PipelineChannelAccount"] is { } accountKey && reader._accountChannels.TryGetValue(accountKey, out var found) ? found : null;
                if (step.Subtype == "send-email-outlook" && channel == "microsoft-outlook")
                    reader.Note(source, "PIPELINE_EMAIL_PLATFORM_SMTP", "The Outlook account is not imported: select or connect a Microsoft Outlook account in the Send an Email step. Until then PowerFlows sends this email through the platform email service (SMTP).", changesExecution: false);
                else if (step.Subtype == "make-request" && channel == "http")
                    return; // MakeRequest() asks for the HTTP connection.
                else if (channel != "quickbase")
                    Incomplete("PIPELINE_ACCOUNT_UNSUPPORTED", "The step's channel account is not a Quickbase account in this file; choose the connection before turning the PowerFlow on.");
            }

            public void NoteUnusedSettings()
            {
                foreach (var key in Inputs.Keys.Where(k => !_usedInputs.Contains(k)))
                    reader.Note(source, "PIPELINE_SETTING_NOT_IMPORTED", $"Input '{key}' is not imported.", changesExecution: true);
                foreach (var key in source.Meta.Keys.Where(k => !_usedMeta.Contains(k)))
                    reader.Note(source, "PIPELINE_SETTING_NOT_IMPORTED", $"Setting '{key}' is not imported.", changesExecution: true);
                foreach (var key in source.DataSources.Keys.Where(k => !_usedSources.Contains(k)))
                    reader.Note(source, "PIPELINE_SETTING_NOT_IMPORTED", $"Data source '{key}' is not imported.", changesExecution: true);
                if (source.Node.Properties.ContainsKey("Filters") && step.Subtype is not ("new-event" or "new-bulk-event" or "search-records" or "if-else"))
                    Incomplete("PIPELINE_FILTER_NOT_VERIFIED", "This step's filter has not been verified for import and was left empty. Set it again before turning the PowerFlow on.");
            }

            // -- shared pieces

            private void Connection()
            {
                Config["connection"] = new PblPipelineConnectionValue();
                Config["connectionPublicId"] = new PblPipelineConnectionValue();
            }

            private void AppAndTable(bool withApp, params string[] tableKeys)
            {
                _usedSources.Add("app");
                _usedSources.Add("table");
                if (withApp && source.AppRef is { } app)
                {
                    Config["app"] = new PblPipelineAppValue(app);
                    Config["appPublicId"] = new PblPipelineAppValue(app);
                    Config["appId"] = new PblPipelineAppValue(app);
                }
                if (source.Table is { } table)
                {
                    foreach (var key in tableKeys)
                        Config[key] = new PblPipelineTableValue(table.App, table.Table);
                }
                else
                {
                    Incomplete("PIPELINE_TABLE_MISSING", "The step's table is not in the file; choose it before turning the PowerFlow on.");
                }
            }

            private static readonly string[] TableKeys = ["tableLabel", "tableId", "tablePublicId"];

            /// <summary>Fields listed by a data source (<c>!Ref</c>s). An address part becomes its whole address field, once — or, with
            /// <paramref name="keepAddressParts"/> (a step's fields for subsequent steps), that part of it (<c>fid_N.city</c>).</summary>
            private List<object?> FieldList(string sourceName, bool keepAddressParts = false)
            {
                _usedSources.Add(sourceName);
                var result = new List<object?>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (!source.DataSources.TryGetValue(sourceName, out var dataSource) || !dataSource.Properties.TryGetValue("Fields", out var raw) || raw is not IEnumerable<object?> refs)
                    return result;

                foreach (var item in refs)
                {
                    if (item is not QblRef fieldRef || fieldRef["App"] is not { } app || fieldRef["Table"] is not { } table || fieldRef["Field"] is not { } field)
                    {
                        Incomplete("PIPELINE_FIELD_NOT_FOUND", $"A field in '{sourceName}' could not be read and was left out.");
                        continue;
                    }
                    var info = reader.Field(app, table, field);
                    if (info is null)
                    {
                        Incomplete("PIPELINE_FIELD_NOT_FOUND", $"Field '{field}' in '{sourceName}' is not in the file and was left out.");
                        continue;
                    }
                    if (info.IsAddressPart && keepAddressParts && reader.Index.AddressOf(info) is { } address && reader.Index.PartKey(info) is { } partKey)
                    {
                        if (seen.Add($"{address.FieldRef}.{partKey}"))
                            result.Add(Value(address) with { Part = partKey });
                        continue;
                    }
                    if (info.IsAddressPart)
                    {
                        if (!_addressNoted)
                        {
                            _addressNoted = true;
                            reader.Note(source, "PIPELINE_ADDRESS_PARTS", "Address part fields (Street, City, State…) are imported as their whole address field.", changesExecution: false);
                        }
                        info = reader.Index.AddressOf(info);
                        if (info is null) continue;
                    }
                    if (seen.Add(info.FieldRef))
                        result.Add(Value(info));
                }
                return result;
            }

            private PblPipelineFieldValue? SingleField(string sourceName)
            {
                var fields = FieldList(sourceName);
                if (fields.Count == 1)
                    return (PblPipelineFieldValue)fields[0]!;
                Incomplete("PIPELINE_FIELD_NOT_FOUND", $"Expected one field in '{sourceName}', found {fields.Count}.");
                return null;
            }

            private static PblPipelineFieldValue Value(QblFieldInfo info) =>
                new(info.AppRef, info.TableRef, info.FieldRef, info.SourceId, info.Label, info.IsBuiltIn);

            /// <summary>A field named by key (<c>Employee_Name</c>, <c>name</c>, <c>id</c>) in <paramref name="table"/>. Fields that
            /// <paramref name="owner"/> lists in its <paramref name="preferSource"/> data source win over other fields with the same key.</summary>
            private QblFieldInfo? FieldByKey((string App, string Table) table, string key, SourceStep? owner = null, string? preferSource = null)
            {
                var fields = reader.Index.Fields(table.App, table.Table);
                if (BuiltInKeys.TryGetValue(key, out var builtInType) && fields.Where(f => f.Type == builtInType).ToList() is [var builtIn])
                    return builtIn;

                var wanted = NormalizeKey(key);
                var candidates = fields.Where(f => !f.IsAddressPart && NormalizeKey(f.Label) == wanted).ToList();
                if (candidates.Count == 0)
                {
                    // Quickbase keys drop the separators of some labels ("TCheckbox" → T_Checkbox): compare letters and digits only.
                    var compact = wanted.Replace("_", "", StringComparison.Ordinal);
                    candidates = fields.Where(f => !f.IsAddressPart && NormalizeKey(f.Label).Replace("_", "", StringComparison.Ordinal) == compact).ToList();
                }
                // Quickbase gives a label whose key is already taken (a field labelled "ID" next to the built-in id) a numbered
                // key: "ID" → id_0. Only when exactly one user field has the key without its number.
                if (candidates.Count == 0 && Regex.Match(wanted, @"^(.+)_\d+$") is { Success: true } numbered)
                {
                    var stem = numbered.Groups[1].Value;
                    var users = fields.Where(f => !f.IsAddressPart && !f.IsBuiltIn && NormalizeKey(f.Label) == stem).ToList();
                    if (users.Count == 1) candidates = users;
                }
                if (candidates.Count > 1 && owner is not null && preferSource is not null && owner.DataSources.TryGetValue(preferSource, out var preferred)
                    && preferred.Properties.TryGetValue("Fields", out var raw) && raw is IEnumerable<object?> refs)
                {
                    var listed = refs.OfType<QblRef>().Select(r => r["Field"]).ToHashSet(StringComparer.Ordinal);
                    candidates = candidates.Where(c => listed.Contains(c.FieldRef)).ToList();
                }
                return candidates.Count == 1 ? candidates[0] : null;
            }

            private QblFieldInfo? AddressPart((string App, string Table) table, string key)
            {
                // Quickbase names a part after its address field: "T Address" + "City" → T_Address_City.
                static string Compact(string text) => NormalizeKey(text).Replace("_", "", StringComparison.Ordinal);
                var wanted = Compact(key);
                var found = reader.Index.Fields(table.App, table.Table).FirstOrDefault(f => f.IsAddressPart
                    && (Compact(f.Label) == wanted || (reader.Index.AddressOf(f) is { } address && Compact(address.Label + " " + f.Label) == wanted)));
                // A numbered address key (Full_Address_0_City: Quickbase numbers a key that is already taken): only when one address
                // field of the table has that name.
                if (found is null && Regex.Match(key, @"^(.+?)_\d+_(.+)$") is { Success: true } numbered)
                {
                    var stem = Compact(numbered.Groups[1].Value);
                    var addresses = reader.Index.Fields(table.App, table.Table).Where(f => f.IsAddressPart && reader.Index.AddressOf(f) is { } a && Compact(a.Label) == stem)
                        .Select(f => reader.Index.AddressOf(f)!.FieldRef).Distinct().Count();
                    if (addresses == 1) found = AddressPart(table, numbered.Groups[1].Value + "_" + numbered.Groups[2].Value);
                }
                return found;
            }

            /// <summary>A value that may hold Jinja tokens. Only <c>{{stepId.key}}</c> naming a record field of an earlier, reachable
            /// record step converts (<c>{{steps.stepId.fid_N}}</c>). Anything else empties the value and makes the step a draft.</summary>
            /// <summary><see cref="TextCore"/> after every Quickbase URL in the value was rewritten to its PowerBase address
            /// (<see cref="QuickbaseUrlRewriter"/>): an imported PowerFlow never points at Quickbase.</summary>
            private object Text(object? raw, string where, bool requestUrl = false, string method = "GET", string? body = null)
            {
                var text = raw?.ToString() ?? string.Empty;
                if (QuickbaseUrlRewriter.ContainsQuickbaseUrl(text))
                {
                    var rewritten = QuickbaseUrlRewriter.Rewrite(text, new QuickbaseUrlRewriter.Context(
                        id => reader.Index.TableByQbId(id) is not null, id => reader.Index.AppByQbId(id) is not null, requestUrl, method, body));
                    foreach (var note in rewritten.Notes)
                        reader.Note(source, "PIPELINE_QUICKBASE_URL_REPLACED", $"{where}: {note}.", changesExecution: false);
                    foreach (var problem in rewritten.Problems)
                        Incomplete("PIPELINE_QUICKBASE_URL_REVIEW", $"{where}: {problem}. Check it before turning the PowerFlow on.");
                    text = rewritten.Text;
                }
                return Markers(TextCore(text, where));
            }

            /// <summary>The imported ids and PowerBase addresses the URL rewriter left as markers, as table / app / site values.</summary>
            private object Markers(object value)
            {
                List<object>? Expand(string text)
                {
                    // Inside a formula value the markers are JSON-escaped; the ids written in their place are plain text there too.
                    if (text.Contains(@"\u0001", StringComparison.Ordinal))
                        text = text.Replace(@"\u0001", "\u0001").Replace(@"\u0002", "\u0002");
                    if (text.IndexOf(QuickbaseUrlRewriter.MARK_START) < 0) return null;
                    var parts = new List<object>();
                    foreach (var (literal, kind, id) in QuickbaseUrlRewriter.Split(text))
                    {
                        if (literal is not null) { parts.Add(literal); continue; }
                        switch (kind)
                        {
                            case QuickbaseUrlRewriter.TABLE when reader.Index.TableByQbId(id!) is { } t: parts.Add(new PblPipelineTableValue(t.App, t.Table)); break;
                            case QuickbaseUrlRewriter.APP_OF_TABLE when reader.Index.TableByQbId(id!) is { } t: parts.Add(new PblPipelineAppValue(t.App)); break;
                            case QuickbaseUrlRewriter.APP when reader.Index.AppByQbId(id) is { } a: parts.Add(new PblPipelineAppValue(a)); break;
                            case QuickbaseUrlRewriter.ANY_APP when (reader.Index.AppByQbId(id) ?? reader.Index.Apps.FirstOrDefault()) is { } a: parts.Add(new PblPipelineAppValue(a)); break;
                            case QuickbaseUrlRewriter.SITE: parts.Add(new PblPipelineSiteValue(Api: false)); break;
                            case QuickbaseUrlRewriter.API: parts.Add(new PblPipelineSiteValue(Api: true)); break;
                            default: parts.Add(string.Empty); break;
                        }
                    }
                    return parts;
                }
                switch (value)
                {
                    case string text when Expand(text) is { } parts:
                        return new PblPipelineTextValue(parts);
                    case PblPipelineTextValue t when t.Parts.Any(p => p is string s && (s.IndexOf(QuickbaseUrlRewriter.MARK_START) >= 0 || s.Contains(@"\u0001", StringComparison.Ordinal))):
                        var all = new List<object>();
                        foreach (var part in t.Parts)
                            if (part is string s && Expand(s) is { } pieces) all.AddRange(pieces);
                            else all.Add(part);
                        return new PblPipelineTextValue(all);
                    default:
                        return value;
                }
            }

            private object TextCore(string text, string where)
            {
                if (!text.Contains("{{", StringComparison.Ordinal) && !text.Contains("{%", StringComparison.Ordinal))
                    return text;

                // powerflowextraRd: a value using Jinja expressions ({{time.now|date_mdy}}, {{a.name|upper}}, {{CLEAR}} …) becomes a
                // PowerFlow formula value, checked with the formula engine (QblJinjaFormula); what our formulas have no equivalent
                // for is cleared and reported below, as before.
                if (QblJinjaFormula.Translate(text, (string path, out List<object>? refParts, out string? kind) =>
                    {
                        var problem = TokenProblem(TokenPattern.Match("{{" + path + "}}"), out refParts);
                        kind = problem is null ? JinjaKind(_lastFieldType) : null;
                        return problem;
                    }) is { } jinja)
                {
                    if (jinja.Problem is null) return jinja.Value!;
                    Incomplete("PIPELINE_REFERENCE_CLEARED", $"{where} '{text}' was cleared: {jinja.Problem}. Set it again before turning the PowerFlow on.");
                    return string.Empty;
                }

                var parts = new List<object>();
                var last = 0;
                foreach (Match token in TokenPattern.Matches(text))
                {
                    if (token.Index > last)
                        parts.Add(text[last..token.Index]);
                    last = token.Index + token.Length;

                    var problem = TokenProblem(token, out var resolved);
                    if (problem is not null)
                    {
                        Incomplete("PIPELINE_REFERENCE_CLEARED", $"{where} '{text}' was cleared: {problem}. Set it again before turning the PowerFlow on.");
                        return string.Empty;
                    }
                    parts.AddRange(resolved!);
                }
                if (last < text.Length)
                    parts.Add(text[last..]);
                return new PblPipelineTextValue(parts);
            }

            /// <summary>powerflowextraRd: the Quickbase type of the field the last token named, so a Jinja expression reads it as Quickbase
            /// does: a checkbox is a boolean (False when empty), a date or date/time is a UTC datetime.</summary>
            private string? _lastFieldType;

            private static string? JinjaKind(string? fieldType) => fieldType switch
            {
                "QB::Field::Checkbox" or "QB::Field::Checkbox::Formula" => "bool",
                "QB::Field::Date" or "QB::Field::Date::Formula" => "date",
                "QB::Field::DateTime" or "QB::Field::DateTime::Formula" or "QB::Field::DateCreated" or "QB::Field::DateModified" => "datetime",
                "QB::Field::Number" or "QB::Field::Numeric" or "QB::Field::Currency" or "QB::Field::Percent" or "QB::Field::Rating"
                    or "QB::Field::Numeric::Formula" or "QB::Field::Currency::Formula" or "QB::Field::Percent::Formula" => "number",
                _ => null,
            };

            private string? TokenProblem(Match token, out List<object>? parts)
            {
                parts = null;
                _lastFieldType = null;
                if (!token.Value.StartsWith("{{", StringComparison.Ordinal))
                    return "Jinja statements are not supported in PowerFlows";
                var path = PathToken.Match(token.Groups[1].Value);
                if (!path.Success)
                    return $"'{token.Value}' has no PowerFlows equivalent";

                var stepId = path.Groups[1].Value;
                var keys = path.Groups[2].Value.TrimStart('.').Split('.');
                var key = keys[0];

                // {{ERROR.x}}: the failed step's details, only inside a Handle Errors "On Error" branch.
                if (stepId == "ERROR")
                {
                    if (!source.IsInErrorBranch())
                        return "the error details can only be read inside the On Error branch of Handle Errors";
                    if (keys.Length != 1 || !ErrorDetails.Contains(key))
                        return $"error detail '{string.Join('.', keys)}' has no PowerFlows equivalent";
                    parts = [$"{{{{steps.ERROR.{key}}}}}"];
                    return null;
                }

                // {{metadata.x.…}}: a step's Metadata, which PowerFlows keeps as {{steps.x._metadata.…}}.
                if (stepId == "metadata")
                    return MetadataTokenProblem(keys, out parts);
                // powerflowextraRd: {{x.$prev.field}} is Quickbase's documented form of {{metadata.x.record.previous.field}}.
                if (key == "$prev")
                    return MetadataTokenProblem([stepId, "record", "previous", .. keys[1..]], out parts);

                if (!reader._byId.TryGetValue(stepId, out var target))
                    return $"step '{stepId}' is not in the pipeline";
                if (target.Removed)
                    return $"step '{target.Name}' ({target.Label}) was not imported";
                if (!IsReachable(target, source))
                    return $"step '{target.Name}' ({target.Label}) is not reachable from here in PowerFlows";

                // The URL rewriter's rid: the record's PowerBase id (a PowerBase page opens a record by it, not by its number).
                if (key == QuickbaseUrlRewriter.RECORD_KEY && keys.Length == 1)
                {
                    parts = [RecordPlaceholder(target)];
                    return null;
                }
                if (target.Subtype == "pipeline-called")
                    return CallableTokenProblem(target, keys, out parts);
                if (target.Subtype == "make-request")
                {
                    // Quickbase's Make Request outputs: the HTTP status, the body text and the response headers, read from the
                    // request's metadata as the editor does ({{steps.x._metadata.status_code}}); "json.<key>" is a field of the
                    // JSON response, which PowerFlows keeps as the step's output ({{steps.x.<key>}}).
                    var mapped = keys switch
                    {
                        ["status"] => "_metadata.status_code",
                        ["content"] => "_metadata.body.text",
                        ["headers", ..] => "_metadata.response_headers" + string.Concat(keys.Skip(1).Select(k => "." + k)),
                        ["json", .. var rest] when rest.Length > 0 => string.Join('.', rest),
                        _ => null,
                    };
                    if (mapped is null)
                        return $"'{string.Join('.', keys)}' of Make Request '{target.Name}' ({target.Label}) has no PowerFlows equivalent";
                    parts = [$"{{{{steps.{target.RefId}.{mapped}}}}}"];
                    return null;
                }
                if (target.Subtype == "copy-records")
                {
                    // Copy Records' outputs keep their Quickbase names ({{steps.x.records_processed}}).
                    if (keys.Length != 1 || !CopyRecordsOutputs.Contains(key))
                        return $"'{string.Join('.', keys)}' of Copy Records '{target.Name}' ({target.Label}) has no PowerFlows equivalent";
                    parts = [$"{{{{steps.{target.RefId}.{key}}}}}"];
                    return null;
                }
                if (target.Subtype == "webhook")
                {
                    // An Incoming Request's output: what the editor offers ({{steps.x.body}}, {{steps.x.body_params.name}}…).
                    var nested = key is "json" or "url_params" or "body_params" or "jwt_payload";
                    if (!WebhookOutputs.Contains(key) || (keys.Length > 1 && !nested))
                        return $"'{string.Join('.', keys)}' of Incoming Request '{target.Name}' ({target.Label}) has no PowerFlows equivalent";
                    parts = [$"{{{{steps.{target.RefId}.{string.Join('.', keys)}}}}}"];
                    return null;
                }

                // Inside a loop over a collection step, {{collection.key}} is the current item. It is written as the editor writes
                // it, {{steps.<collection>.fid_N}}, which the engine reads from the loop's current item and the field picker shows
                // as that step's field.
                var loop = source.LoopOver(stepId);
                string prefix;
                if (loop is not null && !loop.Removed && target.Table is not null)
                    prefix = $"{{{{steps.{target.RefId}.";
                else if (target.Subtype is "new-event" or "create-record" or "update-record" or "look-up-record" && target.Table is not null)
                    prefix = $"{{{{steps.{target.RefId}.";
                else
                    return $"reading '{string.Join('.', keys)}' from step '{target.Name}' ({target.Label}) has not been verified for import";

                var field = FieldByKey(target.Table!.Value, key, target, "export_fields");
                // One part of an address ({{ad.t_address_city}}) reads that part: {{steps.ref_ad.fid_N.city}}.
                if (field is null && keys.Length == 1 && AddressPart(target.Table!.Value, key) is { } addressPart
                    && reader.Index.AddressOf(addressPart) is { } address && reader.Index.PartKey(addressPart) is { } partKey)
                {
                    parts = [prefix, Value(address) with { Part = partKey }, "}}"];
                    return null;
                }
                if (field is null)
                    return $"field '{key}' of step '{target.Name}' ({target.Label}) was not found";
                if (keys.Length == 1)
                {
                    parts = [prefix, Value(field), "}}"];
                    _lastFieldType = field.Type;
                    return null;
                }
                if (keys.Length == 2 && UserFieldTypes.Contains(field.Type) && UserDetails.Contains(keys[1]))
                {
                    parts = [prefix, Value(field), $".{keys[1]}}}}}"];
                    return null;
                }
                // A file field's File transfer handle, Browser url, File name or File size: {{steps.ref.fid_N.file_name}}.
                if (keys.Length == 2 && field.Type == "QB::Field::FileAttachment" && FileDetails.Contains(keys[1]))
                {
                    parts = [prefix, Value(field), $".{keys[1]}}}}}"];
                    return null;
                }
                return $"'{string.Join('.', keys)}' of step '{target.Name}' ({target.Label}) has no PowerFlows equivalent";
            }

            private static readonly HashSet<string> TriggerContextKeys = new(StringComparer.Ordinal)
                { "app_name", "app_id", "table_name", "table_id", "realm_host", "realm_id", "occurred_at", "user" };
            private static readonly HashSet<string> CopyRecordsOutputs = new(StringComparer.Ordinal)
                { "records_processed", "records_added", "records_updated", "records_unchanged", "error_message", "error_code", "records_skipped", "warnings" };
            private static readonly HashSet<string> CopyCacheKeys = new(StringComparer.Ordinal)
                { "qb_table_cache_hit", "qb_table_cache_expires_at", "qb_table_cache_last_modified_at" };

            /// <summary>Quickbase's per-step Metadata: a record trigger's context / record action / table schema, Search Records'
            /// batch size and loop index, Copy Records' table cache details.</summary>
            private string? MetadataTokenProblem(string[] keys, out List<object>? parts)
            {
                parts = null;
                if (keys.Length < 2)
                    return $"'metadata.{string.Join('.', keys)}' has no PowerFlows equivalent";
                if (!reader._byId.TryGetValue(keys[0], out var target))
                    return $"step '{keys[0]}' is not in the pipeline";
                if (target.Removed)
                    return $"step '{target.Name}' ({target.Label}) was not imported";
                if (!IsReachable(target, source))
                    return $"step '{target.Name}' ({target.Label}) is not reachable from here in PowerFlows";

                var prefix = $"{{{{steps.{target.RefId}._metadata.";
                var rest = keys[1..];
                switch (target.Subtype)
                {
                    case "new-event" when rest is ["context", var detail] && TriggerContextKeys.Contains(detail):
                    case "new-event" when rest is ["record", "action"]:
                    case "search-records" when rest is ["batch", "size"] or ["loop", "index"]:
                    case "copy-records" when rest is [var cache] && CopyCacheKeys.Contains(cache):
                        parts = [prefix + string.Join('.', rest) + "}}"];
                        return null;
                    case "new-event" when rest is ["struct", var fieldKey, "qb_field_id" or "qb_field_type"] && target.Table is not null:
                        if (FieldByKey(target.Table.Value, fieldKey, target, "export_fields") is not { } field)
                            return $"field '{fieldKey}' of step '{target.Name}' ({target.Label}) was not found";
                        parts = [prefix + "struct.", Value(field), $".{rest[2]}}}}}"];
                        return null;
                    // powerflowextraRd: Record → Previous record, {{metadata.a.record.previous.name}} (Quickbase's docs write {{a.$prev.name}}):
                    // the trigger's field before the change, kept by field number — {{steps.a._metadata.record.previous.fid_N}}; a user
                    // field's person detail as {{…previous.fid_N.email}}.
                    case "new-event" when rest is ["record", "previous", var fieldKey, ..] && rest.Length <= 4 && target.Table is not null:
                    {
                        if (FieldByKey(target.Table.Value, fieldKey, target, "export_fields") is not { } prevField)
                            return $"field '{fieldKey}' of step '{target.Name}' ({target.Label}) was not found";
                        if (rest.Length == 4 && !(UserFieldTypes.Contains(prevField.Type) && UserDetails.Contains(rest[3])))
                            return $"'metadata.{string.Join('.', keys)}' of step '{target.Name}' ({target.Label}) has no PowerFlows equivalent";
                        parts = [prefix + "record.previous.", Value(prevField), rest.Length == 4 ? $".{rest[3]}}}}}" : "}}"];
                        return null;
                    }
                }
                return $"'metadata.{string.Join('.', keys)}' of step '{target.Name}' ({target.Label}) has no PowerFlows equivalent";
            }

            /// <summary>A PowerFlow Called trigger's output: its call arguments and <c>calling_pipeline</c>.</summary>
            private string? CallableTokenProblem(SourceStep target, string[] keys, out List<object>? parts)
            {
                parts = null;
                if (keys is ["calling_pipeline", var detail] && CallingPipelineDetails.Contains(detail))
                {
                    parts = [$"{{{{steps.{target.RefId}.calling_pipeline.{detail}}}}}"];
                    return null;
                }
                var definition = CallDefinition(target);
                var argument = keys.Length == 1 ? definition?.Arguments.FirstOrDefault(a => string.Equals(a, keys[0], StringComparison.OrdinalIgnoreCase)) : null;
                if (argument is null)
                    return $"'{string.Join('.', keys)}' is not a call argument of step '{target.Name}' ({target.Label})";
                parts = [$"{{{{steps.{target.RefId}.{argument}}}}}"];
                return null;
            }

            /// <summary>Inputs that each set one field (<c>Employee_Name: "{{aa.name}}"</c>). With <paramref name="addressParts"/> (Create /
            /// Update Record) an input setting one part of an address (<c>T_Address_City</c>) maps to that part (<c>fid_N.city</c>).</summary>
            private List<(PblPipelineFieldValue Field, object? Value)> FieldInputs((string App, string Table)? table, SourceStep? owner, string? preferSource, bool addressParts = false)
            {
                var result = new List<(PblPipelineFieldValue, object?)>();
                foreach (var (key, raw) in Inputs)
                {
                    _usedInputs.Add(key);
                    var field = table is null ? null : FieldByKey(table.Value, key, owner, preferSource);
                    if (field is null)
                    {
                        if (addressParts && table is not null && AddressPart(table.Value, key) is { } addressPart
                            && reader.Index.AddressOf(addressPart) is { } address && reader.Index.PartKey(addressPart) is { } partKey)
                        {
                            result.Add((Value(address) with { Part = partKey }, Text(raw, $"Value of '{key}'")));
                            continue;
                        }
                        if (table is not null && AddressPart(table.Value, key) is { } part)
                            Incomplete("PIPELINE_ADDRESS_PART_INPUT", $"Input '{key}' sets one part ('{part.Label}') of the address field '{reader.Index.AddressOf(part)?.Label}'; PowerFlows sets an address as a whole, so it was left out. Set the address before turning the PowerFlow on.");
                        else
                            Incomplete("PIPELINE_FIELD_NOT_FOUND", $"Input '{key}' does not match a field of the step's table and was left out.");
                        continue;
                    }
                    // A formula, lookup or summary field is calculated by the table: a value written to it would fail, so the
                    // write is left out and reported. The field and its formula are not touched.
                    if (field.IsComputed)
                    {
                        reader.Note(source, "PIPELINE_COMPUTED_FIELD_WRITE", $"Input '{key}' writes to '{field.Label}', a calculated field (formula, lookup or summary) that PowerFlows cannot set; the write was left out.", changesExecution: true);
                        continue;
                    }
                    result.Add((Value(field), Text(raw, $"Value of '{key}'")));
                }
                return result;
            }

            private const int MAX_FILTER_GROUPS = 16;

            /// <summary>A Quickbase filter (<c>composite-expression</c>, postfix AND/OR) as PowerFlows filter groups: the expression is
            /// rewritten as OR-ed groups of AND-ed rules (the editor's boxes), each rule written as the editor writes it. Any condition
            /// that cannot be carried leaves the whole filter empty (a partial filter would match more records), and says why.</summary>
            private bool TryFilter(bool forCondition, out List<List<Dictionary<string, object?>>>? groups)
            {
                groups = null;
                if (!source.Node.Properties.TryGetValue("Filters", out var raw) || raw is not IReadOnlyDictionary<string, object?> filters)
                    return true;

                var expressions = filters.TryGetValue("Expression", out var list) && list is IList<object?> items ? items : null;
                var notation = Scalar(filters, "Notation");
                if (Scalar(filters, "Type") != "composite-expression" || expressions is not { Count: > 0 } || notation is not (null or "postfix"))
                    return FilterNotVerified("This filter shape has not been verified for import and was left empty.");

                // Postfix → a tree → OR-ed groups of AND-ed conditions.
                var stack = new Stack<List<List<IReadOnlyDictionary<string, object?>>>>();
                foreach (var item in expressions)
                {
                    if (item is not IReadOnlyDictionary<string, object?> node)
                        return FilterNotVerified("This filter shape has not been verified for import and was left empty.");
                    switch (Scalar(node, "Type"))
                    {
                        case "relational-expression":
                            stack.Push(Expand(node));
                            break;
                        case "logical-operator" when stack.Count >= 2 && Scalar(node, "Operator")?.ToUpperInvariant() is "AND" or "OR":
                            var right = stack.Pop();
                            var left = stack.Pop();
                            stack.Push(Scalar(node, "Operator")!.ToUpperInvariant() == "OR"
                                ? left.Concat(right).ToList()
                                : left.SelectMany(l => right.Select(r => l.Concat(r).ToList())).ToList());
                            if (stack.Peek().Count > MAX_FILTER_GROUPS)
                                return FilterNotVerified($"This filter has more than {MAX_FILTER_GROUPS} OR-ed groups once expanded and was left empty.");
                            break;
                        default:
                            return FilterNotVerified("This filter shape has not been verified for import and was left empty.");
                    }
                }
                if (stack.Count != 1)
                    return FilterNotVerified("This filter shape has not been verified for import and was left empty.");

                var result = new List<List<Dictionary<string, object?>>>();
                foreach (var group in stack.Pop())
                {
                    var rules = new List<Dictionary<string, object?>>();
                    foreach (var expression in group)
                    {
                        var rule = forCondition ? ConditionRule(expression) : FilterRule(expression);
                        if (rule is null) return false;
                        rules.Add(rule);
                    }
                    result.Add(rules);
                }
                groups = result;
                return true;
            }

            /// <summary>Quickbase operators PowerFlows has no single operator for, written as the rules that mean the same:
            /// <c>between [a, b]</c> → on or after a AND on or before b (<c>!between</c> → before a OR after b, numbers alike);
            /// relative dates <c>more-than / less-than [n, unit, past|future]</c> → before / after today ± n units (a Jinja value the
            /// converter turns into a formula); a custom expression (<c>Operator: true</c>, <c>{{c.status == 200}}</c>) → that
            /// expression equals true. Anything else is one rule as written.</summary>
            private List<List<IReadOnlyDictionary<string, object?>>> Expand(IReadOnlyDictionary<string, object?> node)
            {
                var op = Scalar(node, "Operator") ?? "";
                var negated = op.StartsWith('!');
                var name = negated ? op[1..] : op;
                node.TryGetValue("Value", out var raw);
                Dictionary<string, object?> With(string newOp, object? value)
                {
                    var copy = new Dictionary<string, object?>(node, StringComparer.Ordinal) { ["Operator"] = newOp, ["Value"] = value };
                    return copy;
                }
                if (name == "between" && raw is IList<object?> { Count: >= 2 } range)
                {
                    var numeric = decimal.TryParse(range[0]?.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _);
                    var (lo, hi) = numeric ? ("greater", "less") : ("after", "before");
                    // a ≤ x ≤ b is NOT (x < a) AND NOT (x > b).
                    return negated
                        ? [[With(hi, range[0])], [With(lo, range[1])]]
                        : [[With("!" + hi, range[0]), With("!" + lo, range[1])]];
                }
                if (name is "more-than" or "less-than" && raw is IList<object?> { Count: >= 3 } rel && rel[2]?.ToString() is "past" or "future")
                {
                    // more than n in the past = before (today - n); less than n in the past = after it; the future mirrored.
                    var past = rel[2]!.ToString() == "past";
                    var before = (name == "more-than") == past;
                    return [[With((negated ? "!" : "") + (before ? "before" : "after"), raw)]];
                }
                if (name == "true" && node.ContainsKey("CustomExpr") && raw is string expression && expression.Contains("{{", StringComparison.Ordinal))
                    return [[new Dictionary<string, object?>(StringComparer.Ordinal) { ["Type"] = "custom-expression", ["Value"] = expression, ["Negated"] = negated }]];
                return [[node]];
            }

            private bool FilterNotVerified(string message)
            {
                Incomplete("PIPELINE_FILTER_NOT_VERIFIED", message + " Set it again before turning the PowerFlow on.");
                return false;
            }

            /// <summary>A Search / trigger filter rule as the PowerFlows filter editor writes it.</summary>
            private Dictionary<string, object?>? FilterRule(IReadOnlyDictionary<string, object?> expression)
            {
                if (Scalar(expression, "Type") == "custom-expression")
                {
                    FilterNotVerified("A Search/trigger filter with a custom expression has no PowerFlows equivalent; it was left empty.");
                    return null;
                }
                var key = Scalar(expression, "Field");
                var field = key is null || source.Table is null ? null : FieldByKey(source.Table.Value, key);
                if (field is null)
                {
                    Incomplete("PIPELINE_FIELD_NOT_FOUND", $"Filter field '{key}' does not match a field of the step's table; the filter was left empty.");
                    return null;
                }
                if (MapOperator(expression, FilterCategory(field.Type, expression), forCondition: false, field.Type) is not var (op, value))
                    return null;
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "rule", ["field"] = Value(field), ["operator"] = op, ["value"] = value, ["logicalOp"] = "AND",
                };
            }

            /// <summary>A Condition rule as the condition editor writes it: the tested step's field on the left.</summary>
            private Dictionary<string, object?>? ConditionRule(IReadOnlyDictionary<string, object?> expression)
            {
                if (Scalar(expression, "Type") == "custom-expression")
                {
                    // Quickbase "custom expression" ({{c.status == 200}}): the expression, as a formula, must be true.
                    var formula = Text(Scalar(expression, "Value"), "Condition expression");
                    if (formula is string { Length: 0 }) return null;
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "rule", ["left"] = formula, ["op"] = expression.TryGetValue("Negated", out var neg) && neg is true ? "not-equals" : "equals", ["right"] = "true",
                    };
                }
                var targetId = Scalar(expression, "Target");
                var key = Scalar(expression, "Field");
                if (targetId is null || !reader._byId.TryGetValue(targetId, out var tested))
                {
                    FilterNotVerified("The condition does not name the step it tests; it was left empty.");
                    return null;
                }
                if (key is null)
                {
                    // "The step has a result": a record step's record always has a Record ID; a Search counts what it found.
                    if (tested.Subtype is "search-records" && tested.RefId.Length > 0)
                        return SearchFoundRule(expression, tested);
                    if (tested.Subtype is "new-event" or "look-up-record" or "create-record" or "update-record")
                        key = "id";
                    else
                    {
                        FilterNotVerified($"The condition tests whether step '{tested.Name}' ({tested.Label}) returned anything, which has no verified PowerFlows equivalent; it was left empty.");
                        return null;
                    }
                }
                var left = Text($"{{{{{targetId}.{key}}}}}", "Condition field");
                if (left is string { Length: 0 })
                    return null;
                var field = tested.Table is null ? null : FieldByKey(tested.Table.Value, key, tested, "export_fields");
                if (MapOperator(expression, FilterCategory(field?.Type, expression), forCondition: true, field?.Type) is not var (op, value))
                    return null;
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "rule", ["left"] = left, ["op"] = op, ["right"] = value,
                };
            }

            /// <summary>Quickbase "Search returned records / returned none" → the Search's record count ({{steps.x.count}}, which the
            /// engine writes for every Search, also inside a loop over it) is not / is 0.</summary>
            private Dictionary<string, object?>? SearchFoundRule(IReadOnlyDictionary<string, object?> expression, SourceStep search)
            {
                var found = Scalar(expression, "Operator") switch
                {
                    "!empty" or "exists" or "set" => true,
                    "empty" or "!exists" or "!set" => false,
                    _ => (bool?)null,
                };
                if (found is null)
                {
                    Incomplete("PIPELINE_FILTER_OPERATOR", $"Filter operator '{Scalar(expression, "Operator")}' on step '{search.Name}' ({search.Label}) has no confirmed PowerFlows equivalent; the filter was left empty.");
                    return null;
                }
                // The Search was not imported: a rule on its count would point at a missing step (and stop every step from saving).
                if (search.Removed)
                {
                    Incomplete("PIPELINE_REFERENCE_CLEARED", $"The rule on step '{search.Name}' ({search.Label}) was cleared: that step is not imported. Set it again before turning the PowerFlow on.");
                    return null;
                }
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "rule", ["left"] = $"{{{{steps.{search.RefId}.count}}}}", ["op"] = found.Value ? "not-equals" : "equals", ["right"] = "0",
                };
            }

            /// <summary>Quickbase compares an "on date" filter on the day in the filter's time zone. For a Date field (a calendar day, no
            /// time) the zone changes nothing, so the day is kept as it is. For a Date/Time field a fixed day becomes the instant its
            /// midnight starts in that zone — the value the editor's date picker stores — and a Search / trigger filter matches the 24
            /// hours from there, i.e. that day in the zone. A day that comes from a step ({{…}}) is only known when the PowerFlow
            /// runs, and a condition compares instants differently, so those keep the day and say the zone was not applied.</summary>
            private object? OnDateInZone(object? day, string zone, string? fieldType, bool forCondition)
            {
                var kind = fieldType?.Split("::") is { Length: >= 3 } parts ? parts[2] : null;
                if (kind is "Date" or "WorkDate")
                    return day;
                var text = day?.ToString()?.Trim();
                if (kind is "DateTime" or "DateCreated" or "DateModified" && !forCondition
                    && !string.IsNullOrEmpty(text) && !text.Contains("{{", StringComparison.Ordinal)
                    && DateTime.TryParseExact(text, ["yyyy-MM-dd", "MM-dd-yyyy", "MM/dd/yyyy", "M/d/yyyy", "yyyy/MM/dd"],
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                    && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var timeZone))
                {
                    var midnight = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), timeZone);
                    return midnight.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                }
                reader.Note(source, "PIPELINE_FILTER_TIME_ZONE", $"The filter compares the day in the time zone '{zone}', which can only be applied to a fixed day on a Date/Time field; this one is compared in the app's time zone.", changesExecution: true);
                return day;
            }

            /// <summary>The editor's operator list a field gets (NUMBER, DATE, BOOLEAN or TEXT). A field of unknown type is judged by
            /// the operator and value: an ordering of a number compares numbers, of anything else dates.</summary>
            private static string FilterCategory(string? type, IReadOnlyDictionary<string, object?> expression)
            {
                var name = type?.Split("::") is { Length: >= 3 } parts ? parts[2] : null;
                switch (name)
                {
                    case "Number" or "Numeric" or "Currency" or "Percent" or "Rating" or "Duration" or "RecordID": return "NUMBER";
                    case "Date" or "DateTime" or "DateCreated" or "DateModified" or "WorkDate" or "TimeOfDay": return "DATE";
                    case "Checkbox": return "BOOLEAN";
                    case null or "Lookup" or "Summary" or "Unsupported":
                        var op = Scalar(expression, "Operator")?.TrimStart('!');
                        expression.TryGetValue("Value", out var value);
                        if (op is "after" or "before" or "on_date") return "DATE";
                        if (op is "greater" or "less") return decimal.TryParse(value?.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _) ? "NUMBER" : "DATE";
                        return "TEXT";
                    default: return "TEXT";
                }
            }

            /// <summary>A Quickbase filter operator and value → the PowerFlows operator the editor offers for the field's type (the
            /// Search/trigger filter editor and the condition editor use different names) and the value it stores.</summary>
            private (string Op, object Value)? MapOperator(IReadOnlyDictionary<string, object?> expression, string category, bool forCondition, string? fieldType = null)
            {
                var op = Scalar(expression, "Operator") ?? string.Empty;
                expression.TryGetValue("Value", out var raw);
                string Pick(string filter, string condition) => forCondition ? condition : filter;
                bool? flag = raw is bool b ? b : bool.TryParse(raw?.ToString(), out var parsed) && category == "BOOLEAN" ? parsed : null;
                const string NONE = "";

                // Quickbase writes the opposite of an operator with a leading "!" (equals / !equals, set / !set): each operator maps
                // with its exact opposite, so "!less" is "greater than or equal" and "!before" is "on or after".
                var negated = op.StartsWith('!');
                var name = negated ? op[1..] : op;
                (string Op, bool Unary)? mapped = name switch
                {
                    "is" when flag is { } value => ((value != negated) ? "is_true" : "is_false", true),
                    "equals" or "is" or "on_date" => negated ? (Pick("is-not", "not-equals"), false) : (Pick("is", "equals"), false),
                    "contains" => (negated ? "not-contains" : "contains", false),
                    "starts" => (negated ? "not-starts-with" : "starts-with", false),
                    "ends" => (negated ? "not-ends-with" : "ends-with", false),
                    "set" or "exists" => negated ? (Pick("is-empty", "is_blank"), true) : (Pick("is-not-empty", "is_not_blank"), true),
                    "empty" => negated ? (Pick("is-not-empty", "is_not_blank"), true) : (Pick("is-empty", "is_blank"), true),
                    "greater" when category == "NUMBER" => negated ? (Pick("less_than_or_equals", "less-than-or-equal"), false) : (Pick("greater_than", "greater-than"), false),
                    "less" when category == "NUMBER" => negated ? (Pick("greater_than_or_equals", "greater-than-or-equal"), false) : (Pick("less_than", "less-than"), false),
                    "greater" or "after" => negated ? (Pick("is-on-or-before", "on-or-before"), false) : (Pick("is-after", "after"), false),
                    "less" or "before" => negated ? (Pick("is-on-or-after", "on-or-after"), false) : (Pick("is-before", "before"), false),
                    _ => null,
                };
                if (mapped is null)
                {
                    Incomplete("PIPELINE_FILTER_OPERATOR", $"Filter operator '{op}' has no confirmed PowerFlows equivalent; the filter was left empty.");
                    return null;
                }
                if (mapped.Value.Unary)
                    return (mapped.Value.Op, NONE);
                string? zoneOfValue = null;
                if (name == "on_date" && raw is IList<object?> { Count: > 0 } onDate)
                {
                    raw = onDate[0];
                    if (onDate.Count > 1 && onDate[1]?.ToString() is { Length: > 0 } zone)
                    {
                        zoneOfValue = zone;
                        if (RelativeDay(raw, zone, fieldType) is { } relativeDay)
                            return (mapped.Value.Op, relativeDay);
                        raw = OnDateInZone(raw, zone, fieldType, forCondition);
                    }
                }
                if (category == "DATE")
                {
                    // [n, days, past] (Quickbase's relative date) or {{time.today - time.delta(days=n)}}: PowerFlows' own relative
                    // date, which the editor shows as "n day(s) in the past" and the engine works out when the PowerFlow runs.
                    if (raw is IList<object?> { Count: >= 3 } relative && RelativeList(relative) is { } fromList)
                        return (mapped.Value.Op, fromList);
                    if (raw is IList<object?> { Count: >= 3 } hoursList && HoursFormula(hoursList) is { } hoursJinja)
                        raw = hoursJinja;
                    else if (RelativeDay(raw, zoneOfValue ?? "UTC", fieldType) is { } relativeDay)
                        return (mapped.Value.Op, relativeDay);
                }
                return (mapped.Value.Op, Text(raw, forCondition ? "Condition value" : "Filter value"));
            }

            /// <summary><c>[n, days|weeks, past|future, zone?]</c> → <c>@relative-date|past|n|zone</c> (UTC when the file gives no zone,
            /// as Quickbase's own <c>time.today</c>). Months, years and hours have no day count: null.</summary>
            private static string? RelativeList(IList<object?> value)
            {
                if (!int.TryParse(value[0]?.ToString(), out var n) || n < 0) return null;
                var days = value[1]?.ToString()?.TrimEnd('s') switch { "day" => n, "week" => n * 7, _ => (int?)null };
                if (days is null || value[2]?.ToString() is not ("past" or "future") || days > 365000) return null;
                var zone = value.Count > 3 && value[3]?.ToString() is { Length: > 0 } z && TimeZoneInfo.TryFindSystemTimeZoneById(z, out _) ? z : "UTC";
                return Relative(days.Value == 0 ? "today" : value[2]!.ToString()!, days.Value, zone);
            }

            /// <summary><c>[n, hours|minutes, past|future]</c> → a Jinja value (now ± n), turned into a formula like any other.</summary>
            private static string? HoursFormula(IList<object?> value)
            {
                if (!decimal.TryParse(value[0]?.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n)) return null;
                var unit = value[1]?.ToString()?.TrimEnd('s') switch { "hour" => "hours", "minute" => "minutes", "month" => "months", "year" => "years", _ => null };
                if (unit is null || value[2]?.ToString() is not ("past" or "future")) return null;
                var basis = unit is "hours" or "minutes" ? "time.now" : "time.today";
                return $"{{{{{basis} {(value[2]!.ToString() == "past" ? "-" : "+")} time.delta({unit}={n.ToString(System.Globalization.CultureInfo.InvariantCulture)})}}}}";
            }

            private static readonly Regex TodayJinja = new(
                @"^\{\{\s*\(?\s*time\.(?<base>today|now)\s*(?:(?<sign>[-+])\s*time\.delta\(\s*(?<unit>days|weeks)\s*=\s*(?<n>\d+)\s*\))?\s*\)?\s*(?<filter>\|\s*date_(?:ymd|mdy|dmy)(?:\([^)]*\))?)?\s*\}\}$",
                RegexOptions.Compiled);

            /// <summary>A Jinja day relative to today ({{time.today}}, {{(time.today - time.delta(days=30))|date_ymd}}, {{time.now|date_mdy}})
            /// → <c>@relative-date|today|0|zone</c> / <c>past|30</c> / <c>future|n</c>. <c>time.now</c> without a date filter is an instant,
            /// kept as it is unless the field holds a day (a Date field).</summary>
            private static string? RelativeDay(object? raw, string zone, string? fieldType)
            {
                if (raw is not string text || TodayJinja.Match(text.Trim()) is not { Success: true } m) return null;
                var dateField = fieldType?.Split("::") is { Length: >= 3 } parts && parts[2] is "Date" or "WorkDate";
                if (m.Groups["base"].Value == "now" && !m.Groups["filter"].Success && !dateField) return null;
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _)) zone = "UTC";
                var n = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) * (m.Groups["unit"].Value == "weeks" ? 7 : 1) : 0;
                if (n == 0) return Relative("today", 0, zone);
                return Relative(m.Groups["sign"].Value == "-" ? "past" : "future", n, zone);
            }

            private static string Relative(string mode, int days, string zone) => $"@relative-date|{mode}|{(mode == "today" ? 0 : days)}|{zone}";

            private void Filters()
            {
                if (TryFilter(forCondition: false, out var groups) && groups is not null)
                {
                    Config["isSimpleFilter"] = true;
                    // The editor saves both shapes: the groups (boxes OR-ed, rules AND-ed) and the flat list the step header counts.
                    Config["filters"] = groups.SelectMany(g => g).Select(r => (object?)new Dictionary<string, object?>(r, StringComparer.Ordinal)).ToList();
                    Config["filterGroups"] = groups
                        .Select(g => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) { ["logicalOp"] = "AND", ["rules"] = g.Cast<object?>().ToList() })
                        .ToList();
                }
            }

            private SourceStep Target => reader._byId[source.TargetId!];

            /// <summary>The PowerBase id of the record step <paramref name="target"/> works on, as read from this step.</summary>
            private string RecordPlaceholder(SourceStep target)
            {
                if (target.Subtype is "search-records" or "new-bulk-event" && target.Id is not null
                    && source.LoopOver(target.Id) is { Removed: false } loop && loop.RefId.Length > 0)
                    return $"{{{{steps.{loop.RefId}.item.RecordPublicId}}}}";
                return target.Subtype switch
                {
                    "new-event" => "{{trigger.RecordPublicId}}",
                    "create-record" => $"{{{{steps.{target.RefId}.CreatedRecordPublicId}}}}",
                    "update-record" => $"{{{{steps.{target.RefId}.UpdatedRecordPublicId}}}}",
                    _ => $"{{{{steps.{target.RefId}.RecordPublicId}}}}",
                };
            }

            private string TargetRecordPlaceholder()
            {
                var target = Target;
                // The record the enclosing loop is on, as the editor writes a Loop target.
                if (target.Subtype is "search-records" or "new-bulk-event" && reader.LoopItemTarget(source) is { } loop)
                    return $"{{{{steps.{loop.RefId}.item.RecordPublicId}}}}";
                return target.Subtype switch
                {
                    "new-event" => "{{trigger.RecordPublicId}}",
                    "create-record" => $"{{{{steps.{target.RefId}.CreatedRecordPublicId}}}}",
                    "update-record" => $"{{{{steps.{target.RefId}.UpdatedRecordPublicId}}}}",
                    _ => $"{{{{steps.{target.RefId}.RecordPublicId}}}}",
                };
            }

            // -- steps

            public void NewEvent()
            {
                Connection();
                AppAndTable(withApp: true, TableKeys);
                _usedMeta.Add("On_Add_Record");
                _usedMeta.Add("On_Modify_Record");
                _usedMeta.Add("On_Delete_Record");
                Config["triggerOnAdded"] = source.Meta.TryGetValue("On_Add_Record", out var added) && IsTrue(added);
                // Quickbase omits a flag left at its default: modify is on unless the file says On_Modify_Record: false.
                Config["triggerOnModified"] = !source.Meta.TryGetValue("On_Modify_Record", out var modified) || IsTrue(modified);
                Config["triggerOnDeleted"] = source.Meta.TryGetValue("On_Delete_Record", out var deleted) && IsTrue(deleted);
                // The single-event triggers ("Record Created" / "Record Updated") are an On New Event for that one event.
                if (source.Selector is "on_create" or "on_update" or "on_delete")
                {
                    Config["triggerOnAdded"] = source.Selector == "on_create";
                    Config["triggerOnModified"] = source.Selector == "on_update";
                    Config["triggerOnDeleted"] = source.Selector == "on_delete";
                }
                if (Config["triggerOnAdded"] is false && Config["triggerOnModified"] is false && Config["triggerOnDeleted"] is false)
                    Incomplete("PIPELINE_TRIGGER_NO_EVENT", "No event (added, modified or deleted) was imported for the trigger; choose one before turning the PowerFlow on.");
                Config["subsequentFields"] = FieldList("export_fields", keepAddressParts: true);

                // "Trigger on all fields" off: only changes to the fields in trigger_fields start the PowerFlow.
                _usedMeta.Add("Trigger_On_All_Fields");
                if (source.Meta.TryGetValue("Trigger_On_All_Fields", out var allFields) && !IsTrue(allFields))
                {
                    var monitored = FieldList("trigger_fields");
                    Config["triggerOnAnyField"] = false;
                    Config["triggerFields"] = monitored;
                    Config["monitoredFields"] = new List<object?>(monitored);
                    if (monitored.Count == 0)
                        Incomplete("PIPELINE_TRIGGER_FIELDS_MISSING", "The trigger watches specific fields, but none are in the file; choose them before turning the PowerFlow on.");
                }
                else
                {
                    // The empty lists the trigger editor writes, so opening the step is not a change.
                    Config["triggerOnAnyField"] = true;
                    Config["triggerFields"] = new List<object?>();
                    Config["monitoredFields"] = new List<object?>();
                    if (source.DataSources.ContainsKey("trigger_fields"))
                        _usedSources.Add("trigger_fields");
                }

                TriggerLimit();
                Filters();
            }

            /// <summary>Quickbase "Set a maximum" number of changed records → PowerFlows' record limit.</summary>
            private void TriggerLimit()
            {
                _usedMeta.Add("Allow_Triggers");
                _usedMeta.Add("Triggers_Threshold");
                var mode = source.Meta.TryGetValue("Allow_Triggers", out var rawMode) ? rawMode?.ToString() : null;
                source.Meta.TryGetValue("Triggers_Threshold", out var rawThreshold);
                Config["limitRecords"] = false; // what the trigger editor writes when there is no maximum
                if (string.IsNullOrWhiteSpace(mode) || !mode.Contains("maximum", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(mode) && rawThreshold is not null)
                        reader.Note(source, "PIPELINE_SETTING_NOT_IMPORTED", $"Trigger setting '{mode}' ({rawThreshold}) has no PowerFlows equivalent and is not imported.", changesExecution: true);
                    return;
                }
                if (int.TryParse(rawThreshold?.ToString(), out var threshold) && threshold > 0)
                {
                    Config["limitRecords"] = true;
                    Config["maxRecords"] = threshold;
                }
                else
                {
                    Config["limitRecords"] = true;
                    Incomplete("PIPELINE_TRIGGER_LIMIT_INVALID", $"The maximum number of records '{rawThreshold}' cannot be used; set it before turning the PowerFlow on.");
                }
            }

            public void SearchRecords()
            {
                Connection();
                AppAndTable(withApp: true, TableKeys);
                Config["subsequentFields"] = FieldList("export_fields", keepAddressParts: true);
                if (Inputs.TryGetValue("Limit", out var limit))
                {
                    _usedInputs.Add("Limit");
                    if (int.TryParse(limit?.ToString(), out var maxResults) && maxResults > 0)
                        Config["maxResults"] = maxResults;
                    else
                        Incomplete("PIPELINE_INPUT_INVALID", $"The record limit '{limit}' cannot be used; set it before turning the PowerFlow on.");
                }
                Filters();
            }

            public void LookUpRecord()
            {
                Connection();
                AppAndTable(withApp: true, TableKeys);
                Config["subsequentFields"] = FieldList("export_fields", keepAddressParts: true);
                _usedInputs.Add("Id");
                if (Inputs.TryGetValue("Id", out var id))
                {
                    Config["recordIdValue"] = Text(id, "Record ID");
                    return;
                }
                // Quickbase looks the record up by its table's key field, named after it (Special_Project_Code: …).
                var key = source.Table is { } table ? reader.Index.KeyField(table.App, table.Table) : null;
                var keyInput = key is null ? null : Inputs.Keys.FirstOrDefault(k => string.Equals(NormalizeKey(k), NormalizeKey(key.Label), StringComparison.OrdinalIgnoreCase));
                if (key is not null && keyInput is not null)
                {
                    _usedInputs.Add(keyInput);
                    Config["lookupFieldId"] = Value(key);
                    Config["recordIdValue"] = Text(Inputs[keyInput], key.Label);
                }
                else
                    Incomplete("PIPELINE_INPUT_MISSING", key is null && source.Table is null
                        ? "The table to look up in is not in the file, so its key field is unknown; set the look up field and value before turning the PowerFlow on."
                        : "The record id or key field value to look up is not in the file; set it before turning the PowerFlow on.");
            }

            public void CreateRecord()
            {
                Connection();
                AppAndTable(withApp: true, TableKeys);
                _usedSources.Add("export_fields");
                Config["fieldMappings"] = Mappings(FieldInputs(source.Table, source, "export_fields", addressParts: true));
            }

            public void UpdateRecord()
            {
                Connection();
                AppAndTable(withApp: false, TableKeys);
                Config["targetRecordId"] = TargetRecordPlaceholder();
                Config["fieldMappings"] = Mappings(FieldInputs(source.Table, Target, "export_fields", addressParts: true));
            }

            public void DeleteRecord()
            {
                Connection();
                AppAndTable(withApp: false, TableKeys);
                Config["targetRecordId"] = TargetRecordPlaceholder();
            }

            public void PrepareBulkUpsert()
            {
                Connection();
                AppAndTable(withApp: true, TableKeys);
                Config["fields"] = FieldList("export_fields", keepAddressParts: true);
                Config["mergeField"] = (object?)SingleField("merge_field") ?? string.Empty;
            }

            public void AddBulkUpsertRow()
            {
                Connection();
                Config["bulkRecordSetStepId"] = Target.RefId;
                Config["rowValues"] = new PblPipelineFieldMapValue(FieldInputs(source.Table, Target, "export_fields", addressParts: true));
            }

            public void CommitUpsert()
            {
                Connection();
                Config["bulkRecordSetStepId"] = Target.RefId;
            }

            public void CopyRecords()
            {
                Connection();
                var destination = reader.TableOf(source.DataSources, "table");
                var origin = reader.TableOf(source.DataSources, "source_table");
                _usedSources.Add("table");
                _usedSources.Add("source_table");
                if (destination is null || origin is null)
                    Incomplete("PIPELINE_TABLE_MISSING", "The source or destination table is not in the file; choose it before turning the PowerFlow on.");
                if (origin is { } from) Config["sourceTable"] = new PblPipelineTableValue(from.App, from.Table);
                if (destination is { } to) Config["destinationTable"] = new PblPipelineTableValue(to.App, to.Table);

                // Each source field is paired with the destination field in the data source named after its key
                // (source field "Name" → "qb_field_for_name"), in the source fields' order. Every source field is kept, as in
                // Quickbase; a pair PowerFlows cannot copy keeps an empty destination (the step stays a draft) and says why.
                var sourceFields = new List<object?>();
                var destinationFields = new List<object?>();
                var copiedFrom = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (PblPipelineFieldValue sourceField in FieldList("source_table_export_fields").Cast<PblPipelineFieldValue>())
                {
                    var pairName = "qb_field_for_" + NormalizeKey(sourceField.Label);
                    _usedSources.Add(pairName);
                    sourceFields.Add(sourceField);
                    var destinationField = source.DataSources.ContainsKey(pairName) ? SingleField(pairName) : null;
                    if (destinationField is null)
                    {
                        destinationFields.Add(string.Empty);
                        Incomplete("PIPELINE_FIELD_NOT_FOUND", $"Source field '{sourceField.Label}' has no destination field in the file; it is kept with no destination. Choose one before turning the PowerFlow on.");
                        continue;
                    }
                    // PowerFlows only copies into writable fields: not calculated fields or attachments. Built-in fields (Record ID#,
                    // Date Created/Modified, Record Owner, Last Modified By) can be mapped as in Quickbase; the copy keeps the
                    // destination's own values for all but the key.
                    var destinationInfo = reader.Field(destinationField.AppRef, destinationField.TableRef, destinationField.FieldRef);
                    if (destinationInfo is not null && (destinationInfo.IsComputed || destinationInfo.Type == "QB::Field::FileAttachment"))
                    {
                        destinationFields.Add(string.Empty);
                        Incomplete("PIPELINE_COPY_DESTINATION_READ_ONLY", $"Source field '{sourceField.Label}' copies into '{destinationField.Label}', which PowerFlows cannot write (read-only, calculated or a file attachment). The source field is kept with no destination; choose a writable one or remove the column before turning the PowerFlow on.");
                        continue;
                    }
                    // PowerFlows copies into each destination field once; the first source column keeps it.
                    if (!copiedFrom.TryAdd(destinationField.FieldRef, sourceField.Label))
                    {
                        destinationFields.Add(string.Empty);
                        Incomplete("PIPELINE_COPY_DESTINATION_REPEATED", $"Source field '{sourceField.Label}' copies into '{destinationField.Label}', which '{copiedFrom[destinationField.FieldRef]}' already fills; PowerFlows allows each destination field once. The source field is kept with no destination; choose another or remove the column before turning the PowerFlow on.");
                        continue;
                    }
                    destinationFields.Add(destinationField);
                }
                Config["sourceFields"] = sourceFields;
                Config["destinationFields"] = destinationFields;
                Config["mergeField"] = (object?)SingleField("merge_field") ?? string.Empty;
            }

            public void Pause()
            {
                _usedMeta.Add("Duration");
                var text = source.Meta.TryGetValue("Duration", out var raw) ? raw?.ToString() : null;
                Config.Remove("duration");
                Config.Remove("unit");
                Config["durationText"] = text ?? string.Empty;
                try
                {
                    PauseStepConfig.ParseDuration(JsonSerializer.Serialize(new { durationText = text ?? string.Empty }));
                }
                catch (ArgumentException ex)
                {
                    Incomplete("PIPELINE_PAUSE_INVALID", $"Pause duration '{text}' cannot be used in PowerFlows ({ex.Message}).");
                }
            }

            public void Condition()
            {
                var groups = new List<object?>();
                // The condition editor's groups are OR-ed, the rules inside each group AND-ed.
                if (TryFilter(forCondition: true, out var ruleGroups) && ruleGroups is not null)
                    groups.AddRange(ruleGroups.Select(g => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) { ["logicalOp"] = "AND", ["rules"] = g.Cast<object?>().ToList() }));
                else if (!source.Node.Properties.ContainsKey("Filters"))
                    Incomplete("PIPELINE_FILTER_NOT_VERIFIED", "The condition has no filter in the file; set it before turning the PowerFlow on.");
                Config["ruleGroups"] = groups;
            }

            public void Loop()
            {
                Config["loopOverStepId"] = Target.RefId;
            }

            /// <summary>A setting Quickbase keeps in Inputs for one step and in InputsMeta for another.</summary>
            private object? Setting(string name)
            {
                if (Inputs.TryGetValue(name, out var input)) { _usedInputs.Add(name); return input; }
                if (source.Meta.TryGetValue(name, out var meta)) { _usedMeta.Add(name); return meta; }
                return null;
            }

            private static readonly string[] HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

            private void RequestMethod()
            {
                var method = Setting("Method")?.ToString()?.Trim().ToUpperInvariant();
                Config["method"] = method is not null && HttpMethods.Contains(method) ? method : "GET";
                if (method is not null && !HttpMethods.Contains(method))
                    Incomplete("PIPELINE_INPUT_INVALID", $"Request method '{method}' cannot be used; choose it before turning the PowerFlow on.");
            }

            public void MakeRequest()
            {
                if (source.Channel == "http")
                    HttpRequest();
                else if (source.Channel == "webhooks" && !CallsMappableQuickbaseApi() && !QuickbaseUrlRewriter.ContainsQuickbaseUrl(Setting("Url")?.ToString()))
                    WebhookRequest();
                else
                    PowerBaseRequest();
            }

            /// <summary>Quickbase "Make Request" on the Webhooks channel (a full URL, no account) → the editor's HTTP mode: the URL's
            /// origin is the base URL and the rest the path; headers are kept as step headers. No HTTP account is created.</summary>
            private void WebhookRequest()
            {
                Config["requestMode"] = "http";
                Config["authType"] = "No Authentication";
                Config["errorsOption"] = "automatic";
                Config["expectedPayloadType"] = "JSON";
                Config["schemaSampleType"] = "JSON sample";
                Config["validateResponsePayload"] = "Yes";
                Config["accountName"] = string.Empty;
                RequestMethod();
                if (Setting("Body") is { } body) Config["body"] = Text(body, "Body");
                if (Setting("Content_Type") is { } contentType) Config["contentType"] = contentType.ToString();

                var url = Setting("Url")?.ToString()?.Trim();
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
                {
                    Config["baseUrl"] = absolute.GetLeftPart(UriPartial.Authority);
                    Config["path"] = Text(url[Config["baseUrl"]!.ToString()!.Length..].TrimStart('/'), "URL");
                    if (absolute.Host.EndsWith("quickbase.com", StringComparison.OrdinalIgnoreCase))
                        Incomplete("PIPELINE_REQUEST_URL_QUICKBASE", $"The request URL '{url}' calls the Quickbase API, which PowerBase's API does not mirror; it was kept as is. Change it before turning the PowerFlow on.");
                }
                else
                {
                    // A URL built from step values ({{a.url}}) has no fixed origin: kept whole as the path.
                    Config["baseUrl"] = string.Empty;
                    Config["path"] = url is null ? string.Empty : Text(url, "URL");
                    Incomplete(url is null ? "PIPELINE_INPUT_MISSING" : "PIPELINE_INPUT_INVALID",
                        url is null ? "The request URL is not in the file; set it before turning the PowerFlow on."
                                    : $"The request URL '{url}' has no fixed address; set the base URL before turning the PowerFlow on.");
                }

                var headers = RequestHeaders().Select(h => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = h.Name, ["value"] = h.Value }).ToList();
                Config["stepHeaders"] = headers.Count > 0 ? headers : new List<object?> { new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = "", ["value"] = "" } };
                Incomplete("PIPELINE_HTTP_CONNECTION_MISSING", "This request has no HTTP account in Quickbase; name and save one for its base URL in the step before turning the PowerFlow on.");
            }

            /// <summary>Quickbase request headers: a "Name" and a "Value" text, one header per line.</summary>
            private List<(string Name, object Value)> RequestHeaders()
            {
                var headers = new List<(string Name, object Value)>();
                if (Setting("Headers") is not IDictionary<string, object?> pairs)
                    return headers;
                var names = (pairs.TryGetValue("Name", out var n) ? n?.ToString() : null)?.Split('\n') ?? [];
                var values = (pairs.TryGetValue("Value", out var v) ? v?.ToString() : null)?.Split('\n') ?? [];
                for (var i = 0; i < names.Length; i++)
                    if (names[i].Trim().Length > 0)
                        headers.Add((names[i].Trim(), i < values.Length ? Text(values[i].Trim(), $"Header '{names[i].Trim()}'") : string.Empty));
                return headers;
            }

            /// <summary>Quickbase "Make Request" (HTTP channel) → the editor's HTTP mode. The HTTP account (base URL and
            /// authentication) is not in the file, so the step waits for its connection.</summary>
            private void HttpRequest()
            {
                Config["requestMode"] = "http";
                Config["authType"] = "No Authentication";
                Config["errorsOption"] = "automatic";
                RequestMethod();
                var path = Setting("Path");
                Config["path"] = path is null ? string.Empty : Text(path, "Path");
                if (Setting("Body") is { } body) Config["body"] = Text(body, "Body");
                if (Setting("Content_Type") is { } contentType) Config["contentType"] = contentType.ToString();

                var payload = Setting("Expected_Payload_Type")?.ToString()?.ToLowerInvariant();
                Config["expectedPayloadType"] = payload switch
                {
                    null or "json" => "JSON", "xml" => "XML", "yaml" => "YAML", "text" => "TEXT", "raw" => "Raw",
                    _ => Unknown("Expected payload type", payload, "JSON"),
                };
                var schemaType = Setting("Schema_Sample_Type")?.ToString()?.ToLowerInvariant();
                Config["schemaSampleType"] = schemaType switch
                {
                    null or "json-sample" => "JSON sample", "json-schema" => "JSON schema", "xml-sample" => "XML sample",
                    "yaml-sample" => "YAML sample", "yaml-schema" => "YAML schema",
                    _ => Unknown("Schema sample type", schemaType, "JSON sample"),
                };
                Config["schemaSample"] = Setting("Schema_Sample")?.ToString() ?? string.Empty;
                // Quickbase leaves the setting out at its default, which is on.
                Config["validateResponsePayload"] = Setting("Validate_Response_Payload_Type") is { } validate && !IsTrue(validate) ? "No" : "Yes";

                // The account's name is in the file, its base URL and credentials are not (Quickbase never exports them), so the
                // name is kept for the account the user connects; no connection is created here.
                var alias = source.Node.Properties.TryGetValue("PipelineChannelAccount", out var account) && account is QblRef accountRef
                    && accountRef["PipelineChannelAccount"] is { } accountKey && reader._accountAliases.TryGetValue(accountKey, out var found) ? found : null;
                if (alias is null)
                {
                    Incomplete("PIPELINE_HTTP_ACCOUNT_MISSING", "The step's HTTP account is not in the file; connect an HTTP account (name, base URL and authentication) in the step before turning the PowerFlow on.");
                    return;
                }
                Config["accountName"] = alias;
                Incomplete("PIPELINE_HTTP_CONNECTION_MISSING", $"HTTP account '{alias}' is imported by name only: its base URL and authentication are not in the file. Connect it in the step before turning the PowerFlow on.");
            }

            private string Unknown(string setting, string value, string fallback)
            {
                Incomplete("PIPELINE_INPUT_INVALID", $"{setting} '{value}' has no PowerFlows equivalent; '{fallback}' was used. Check it before turning the PowerFlow on.");
                return fallback;
            }

            /// <summary>Quickbase "Make Request" on the Quickbase channel → the editor's PowerBase-account mode. A URL of the
            /// Quickbase API is not kept: PowerBase's API has other endpoints, and calling Quickbase from here would be wrong.</summary>
            private void PowerBaseRequest()
            {
                Connection();
                Config["requestMode"] = "quickbase";
                Config["disableSsl"] = "No";
                // Quickbase's own credentials and realm mean nothing here: the PowerBase account signs the call, and PowerBase rejects
                // a realm that is not the tenant's (and any Authorization header).
                var all = RequestHeaders();
                var dropped = all.Where(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Name.Equals("QB-Realm-Hostname", StringComparison.OrdinalIgnoreCase)
                    || h.Name.Equals("QB-App-Token", StringComparison.OrdinalIgnoreCase)).Select(h => h.Name).ToList();
                if (dropped.Count > 0)
                    reader.Note(source, "PIPELINE_REQUEST_HEADER_DROPPED", $"Quickbase header(s) {string.Join(", ", dropped)} were left out: the PowerBase account authenticates the request.", changesExecution: false);
                var headers = all.Where(h => !dropped.Contains(h.Name)).Select(h => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = h.Name, ["value"] = h.Value }).ToList();
                Config["headersList"] = headers.Count > 0 ? headers : new List<object?> { new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "", ["value"] = "" } };
                RequestMethod();
                Config["contentType"] = Setting("Content_Type")?.ToString() ?? string.Empty;
                Config["body"] = Setting("Body") is { } body ? Text(body, "Body") : string.Empty;

                var url = Setting("Url")?.ToString()?.Trim();
                if (url is not null && QuickbaseUrlRewriter.ContainsQuickbaseUrl(url))
                {
                    if (!url.Contains("{{", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var absolute) && MapQuickbaseApi(absolute, out _))
                        return;
                    // Computed URLs and calls the compatible API does not cover: rewritten (or removed and reported), never kept.
                    Config["url"] = Text(url, "URL", requestUrl: true, Config["method"]?.ToString() ?? "GET", Setting("Body")?.ToString());
                    if (Config["headersList"] is List<object?> hs)
                    {
                        hs.RemoveAll(h => h is Dictionary<string, object?> d && d.GetValueOrDefault("name")?.ToString() is { } n
                            && (n.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || n.Equals("QB-Realm-Hostname", StringComparison.OrdinalIgnoreCase)));
                        if (hs.Count == 0) hs.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "", ["value"] = "" });
                    }
                    if (Config["url"] is string { Length: 0 })
                        Incomplete("PIPELINE_INPUT_MISSING", "The request URL called Quickbase and has no PowerBase equivalent; set the PowerBase endpoint before turning the PowerFlow on.");
                    var bodyIds = QuickbaseApiUrlMap.BodyTableIds(Setting("Body")?.ToString());
                    var unknown = bodyIds.Where(t => reader.Index.TableByQbId(t) is null).ToList();
                    if (unknown.Count > 0)
                        Incomplete("PIPELINE_REQUEST_NEEDS_REVIEW", $"The request body names Quickbase table '{string.Join("', '", unknown)}', which is not one of the imported tables; put the PowerBase table id there before turning the PowerFlow on.");
                    else if (bodyIds.Count > 0)
                        Config["body"] = BodyWithTables(Config["body"], bodyIds);
                }
                else if (url is not null)
                    Config["url"] = Text(url, "URL");
                else
                {
                    Config["url"] = string.Empty;
                    Incomplete("PIPELINE_INPUT_MISSING", "The request URL is not in the file; set it before turning the PowerFlow on.");
                }
            }

            /// <summary>A Webhooks-channel Make Request whose URL is a Quickbase API call PowerBase has an endpoint for: it becomes a
            /// PowerBase-account request instead of an HTTP one.</summary>
            private bool CallsMappableQuickbaseApi()
            {
                var url = Setting("Url")?.ToString()?.Trim();
                if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var absolute) || !QuickbaseApiUrlMap.IsQuickbaseApi(absolute))
                    return false;
                var method = Setting("Method")?.ToString() ?? "GET";
                var body = Setting("Body")?.ToString();
                return QuickbaseApiUrlMap.Map(method, absolute, body, out _) is { } mapped && Resolve(mapped) is not null;
            }

            private List<object>? Resolve(QuickbaseApiUrlMap.Mapped mapped) => Resolve(mapped.Path);

            private List<object>? Resolve(IEnumerable<QuickbaseApiUrlMap.Part> path)
            {
                var parts = new List<object>();
                foreach (var part in path)
                    switch (part)
                    {
                        case QuickbaseApiUrlMap.Literal literal: parts.Add(literal.Text); break;
                        case QuickbaseApiUrlMap.TableId table when reader.Index.TableByQbId(table.QbTableId) is { } found:
                            parts.Add(new PblPipelineTableValue(found.App, found.Table)); break;
                        case QuickbaseApiUrlMap.AppId app when (reader.Index.AppByQbId(app.QbAppId) ?? (reader.Index.Apps.Count == 1 ? reader.Index.Apps.First() : null)) is { } appRef:
                            parts.Add(new PblPipelineAppValue(appRef)); break;
                        case QuickbaseApiUrlMap.XmlDbId db when reader.Index.TableByQbId(db.QbId) is { } dbTable:
                            parts.Add(new PblPipelineTableValue(dbTable.App, dbTable.Table)); break;
                        case QuickbaseApiUrlMap.XmlDbId db when reader.Index.AppByQbId(db.QbId) is { } dbApp:
                            parts.Add(new PblPipelineAppValue(dbApp)); break;
                        default: return null;
                    }
                return parts;
            }

            /// <summary>Quickbase API URL -> the PowerBase endpoint that does the same (relative path, run inside PowerBase by the
            /// PowerBase account). The Quickbase-compatible /v1 endpoints keep Quickbase's path and body; only the table / app ids
            /// (URL and body from/to/tableId) become the imported ones. Quickbase's own auth / realm headers are dropped: the
            /// PowerBase account authenticates the call.</summary>
            private bool MapQuickbaseApi(Uri absolute, out string reason)
            {
                var rawBody = Setting("Body")?.ToString();
                var mapped = QuickbaseApiUrlMap.Map(Config["method"]?.ToString() ?? "GET", absolute, rawBody, out reason);
                if (mapped is null)
                    return false;
                if (Resolve(mapped) is not { } parts)
                {
                    reason = "the Quickbase table or app id it uses is not one of the imported tables";
                    return false;
                }
                var unknownBodyIds = mapped.BodyTableIds.Where(t => reader.Index.TableByQbId(t) is null).ToList();
                if (unknownBodyIds.Count > 0)
                {
                    reason = $"the body names Quickbase table '{string.Join("', '", unknownBodyIds)}', which is not one of the imported tables";
                    return false;
                }
                Config["url"] = parts.All(p => p is string) ? string.Concat(parts) : new PblPipelineTextValue(parts);
                var originalMethod = Config["method"]?.ToString();
                Config["method"] = mapped.Method;
                if (rawBody is not null && mapped.BodyTableIds.Count > 0)
                    Config["body"] = BodyWithTables(Config["body"], mapped.BodyTableIds);
                if (string.IsNullOrWhiteSpace(Config["contentType"]?.ToString()) && mapped.Method != "GET")
                    Config["contentType"] = "application/json";
                if (Config["headersList"] is List<object?> headers)
                {
                    var kept = headers.OfType<Dictionary<string, object?>>()
                        .Where(h => h.GetValueOrDefault("name")?.ToString() is not { } n || !(n.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("QB-Realm-Hostname", StringComparison.OrdinalIgnoreCase) || n.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
                        .Cast<object?>().ToList();
                    Config["headersList"] = kept.Count > 0 ? kept : new List<object?> { new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "", ["value"] = "" } };
                }
                var methodNote = string.Equals(originalMethod, mapped.Method, StringComparison.OrdinalIgnoreCase) ? "" : $" (method {originalMethod} → {mapped.Method}, the only one Quickbase accepts there)";
                reader.Note(source, "PIPELINE_REQUEST_URL_MAPPED",
                    $"The Quickbase API call '{originalMethod} {absolute}' was changed to the PowerBase endpoint '{mapped.Endpoint}'{methodNote}.", changesExecution: false);
                if (mapped.ResponseDiffers)
                    Incomplete("PIPELINE_REQUEST_BODY_FORMAT", $"The request now calls PowerBase '{mapped.Endpoint}', whose request and response format differ from Quickbase's; check the body and the steps that read the response before turning the PowerFlow on.");
                if (mapped.Review is { } review)
                    Incomplete("PIPELINE_REQUEST_NEEDS_REVIEW", $"The request now calls PowerBase '{mapped.Endpoint}', but {review}. Fix it before turning the PowerFlow on.");
                return true;
            }

            /// <summary>The body with each Quickbase table id after from / to / tableId replaced by the imported table's id.</summary>
            private object? BodyWithTables(object? body, IReadOnlyList<string> qbTableIds)
            {
                List<object>? Split(string text) =>
                    QuickbaseApiUrlMap.SplitBody(text, qbTableIds) is { } split ? Resolve(split) : null;
                switch (body)
                {
                    case string text when Split(text) is { } parts:
                        return new PblPipelineTextValue(parts);
                    case PblPipelineTextValue value:
                        var all = new List<object>();
                        foreach (var part in value.Parts)
                            if (part is string piece && Split(piece) is { } pieces) all.AddRange(pieces);
                            else all.Add(part);
                        return new PblPipelineTextValue(all);
                    // Inside an expression the Quickbase id stays: PowerBase's Quickbase-compatible API finds a table by the Quickbase
                    // id stored when it was imported (meta.AppTable.QBTableID), so a call to an imported table works unchanged.
                    case var _ when qbTableIds.All(id => reader.Index.TableByQbId(id) is not null):
                        reader.Note(source, "PIPELINE_REQUEST_QB_TABLE_ID_KEPT", $"The request body names Quickbase table '{string.Join("', '", qbTableIds)}' inside an expression; the id is kept and PowerBase's Quickbase-compatible API finds the imported table by it.", changesExecution: false);
                        return body;
                    default:
                        Incomplete("PIPELINE_REQUEST_NEEDS_REVIEW", $"The request body names Quickbase table '{string.Join("', '", qbTableIds)}' inside an expression; put the imported table's id there before turning the PowerFlow on.");
                        return body;
                }
            }

            /// <summary>Quickbase "Incoming Request" → PowerFlows' webhook trigger. PowerFlows gives it a new URL.</summary>
            public void IncomingRequest()
            {
                var auth = Setting("Authentication_Schema")?.ToString()?.ToLowerInvariant();
                switch (auth)
                {
                    case null or "no-auth": Config["authType"] = "no-auth"; break;
                    case "jwt":
                        Config["authType"] = "jwt";
                        if (Setting("Jwt_Algorithm") is { } algorithm) Config["jwtAlgorithm"] = algorithm.ToString();
                        if (Setting("Public_Key") is { } key) Config["publicKey"] = key.ToString();
                        else Incomplete("PIPELINE_INPUT_MISSING", "The JWT public key is not in the file; enter it before turning the PowerFlow on.");
                        break;
                    default:
                        Incomplete("PIPELINE_INPUT_INVALID", $"Authentication '{auth}' has no PowerFlows equivalent; choose it before turning the PowerFlow on.");
                        break;
                }
                var method = Setting("Method_Type")?.ToString()?.Trim().ToUpperInvariant();
                if (method is null || method.StartsWith("ANY", StringComparison.Ordinal))
                    Config["methodType"] = "ANY BELOW";
                else if (IncomingWebhookConfig.Methods.Contains(method))
                    Config["methodType"] = method;
                else
                    Incomplete("PIPELINE_INPUT_INVALID", $"Request method '{method}' cannot be used; choose it before turning the PowerFlow on.");
                if (Setting("Proxy_Via_Agent") is { } proxy) Config["proxyViaOnPremisesAgent"] = IsTrue(proxy);
                reader.Note(source, "PIPELINE_WEBHOOK_NEW_URL", "PowerFlows gives this Incoming Request its own URL; point the sender at it after import.", changesExecution: true);
            }

            public void HandleErrors()
            {
                var mode = Scalar(source.Node.Properties, "Mode");
                if (string.Equals(mode, "Handle", StringComparison.OrdinalIgnoreCase))
                    Config["fallbackAction"] = "handle";
                else if (string.Equals(mode, "Ignore", StringComparison.OrdinalIgnoreCase))
                    Config["fallbackAction"] = "ignore";
                else
                    Incomplete("PIPELINE_SETTING_NOT_IMPORTED", $"Handle Errors mode '{mode}' has no PowerFlows equivalent; choose it before turning the PowerFlow on.");
            }

            public void UploadFile()
            {
                Config["fileRecordStepId"] = Target.RefId;
                _usedInputs.Add("Name");
                _usedInputs.Add("Url");
                if (Inputs.TryGetValue("Name", out var name))
                    Config["fileName"] = Text(name, "File name");
                if (Inputs.TryGetValue("Url", out var url))
                    Config["fileUrl"] = Text(url, "File URL");
                else
                    Incomplete("PIPELINE_INPUT_MISSING", "The file URL is not in the file; set it before turning the PowerFlow on.");
                Incomplete("PIPELINE_INPUT_MISSING", "The file attachment field to upload into is not in the file; choose it before turning the PowerFlow on.");
            }

            public void ImportWithCsv()
            {
                Connection();
                AppAndTable(withApp: true, TableKeys);
                _usedMeta.Add("Csv_Url");
                if (source.Meta.TryGetValue("Csv_Url", out var url))
                    Config["csvFile"] = Text(url, "CSV URL");
                else
                    Incomplete("PIPELINE_INPUT_MISSING", "The CSV URL is not in the file; set it before turning the PowerFlow on.");

                // PowerFlows reads the CSV through an import template (columns, header row, quote character); Quickbase keeps
                // these on the step, so they are listed for the template instead of being dropped silently.
                var columns = source.DataSources.Keys.Where(k => k.StartsWith("qb_field_for_", StringComparison.Ordinal)).ToList();
                foreach (var column in columns) _usedSources.Add(column);
                var settings = new[] { "Header_Row", "Row_Quote_Char" }.Where(source.Meta.ContainsKey).ToList();
                foreach (var setting in settings) _usedMeta.Add(setting);
                var described = columns.Select(c => $"column '{c["qb_field_for_".Length..]}' → {DescribeField(c)}")
                    .Concat(settings.Select(s => $"{s.Replace('_', ' ')} '{source.Meta[s]}'"));
                Incomplete("PIPELINE_IMPORT_TEMPLATE_MISSING",
                    "PowerFlows imports CSV through an import template, which is not in the file. Choose or create one before turning the PowerFlow on"
                    + (columns.Count + settings.Count > 0 ? $" (Quickbase settings: {string.Join("; ", described)})." : "."));
            }

            private string DescribeField(string sourceName)
            {
                var fields = FieldList(sourceName);
                return fields.Count == 1 && fields[0] is PblPipelineFieldValue field ? $"'{field.Label}'" : "(field not found)";
            }

            private static readonly (string Input, string Key)[] EmailInputs =
            [
                ("To_Addresses", "toAddresses"), ("Cc_Addresses", "ccAddresses"), ("Bcc_Addresses", "bccAddresses"),
                ("From_Address", "fromAddress"), ("Subject", "subject"), ("Body", "body"),
            ];

            public void SendEmail()
            {
                foreach (var (input, key) in EmailInputs)
                {
                    _usedInputs.Add(input);
                    if (Inputs.TryGetValue(input, out var value))
                        Config[key] = Text(value, input.Replace('_', ' '));
                }
                if (!Inputs.ContainsKey("To_Addresses"))
                    Incomplete("PIPELINE_INPUT_MISSING", "The recipient (To) is not in the file; set it before turning the PowerFlow on.");

                _usedInputs.Add("Content_Type");
                _usedInputs.Add("Importance");
                _usedInputs.Add("Save_To_Sent_Items");
                if (Scalar(Inputs, "Content_Type") is { } contentType)
                    Config["contentType"] = string.Equals(contentType, "HTML", StringComparison.OrdinalIgnoreCase) ? "HTML" : "Text";
                if (Scalar(Inputs, "Importance") is { } importance)
                    Config["importance"] = importance.ToLowerInvariant() switch { "low" => "Low", "high" => "High", _ => "Normal" };
                if (Inputs.TryGetValue("Save_To_Sent_Items", out var save))
                    Config["saveToSentItems"] = IsTrue(save) ? "Yes" : "No";

                _usedMeta.Add("Mailbox");
                if (source.Meta.TryGetValue("Mailbox", out var mailbox))
                    Config["sharedMailbox"] = Text(mailbox, "Shared mailbox");
            }

            private static CallablePipelineDefinition? CallDefinition(SourceStep step)
            {
                try { return CallablePipelineDefinition.Parse(Scalar(step.Meta, "Call_Definition")); }
                catch (PipelineNonRetryableException) { return null; }
            }

            /// <summary>The call definition (<c>name(arg1, arg2)</c>) both callable steps share.</summary>
            private CallablePipelineDefinition? Definition()
            {
                _usedMeta.Add("Call_Definition");
                var definition = CallDefinition(source);
                var text = Scalar(source.Meta, "Call_Definition");
                Config["callDefinition"] = definition?.Definition ?? text ?? string.Empty;
                if (definition is null)
                    Incomplete("PIPELINE_CALL_DEFINITION_INVALID", $"Call definition '{text}' cannot be used in PowerFlows; set it before turning the PowerFlow on.");
                else if (definition.Arguments.Contains("calling_pipeline", StringComparer.Ordinal))
                    Incomplete("PIPELINE_CALL_DEFINITION_INVALID", "The call argument 'calling_pipeline' is reserved in PowerFlows; rename it before turning the PowerFlow on.");
                return definition;
            }

            public void PipelineCalled() => Definition();

            public void CallAnotherPipeline()
            {
                var definition = Definition();
                var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var argument in definition?.Arguments ?? [])
                {
                    var input = Inputs.Keys.FirstOrDefault(k => string.Equals(k, argument, StringComparison.OrdinalIgnoreCase));
                    if (input is null)
                    {
                        Incomplete("PIPELINE_INPUT_MISSING", $"No value for call argument '{argument}' is in the file; set it before turning the PowerFlow on.");
                        continue;
                    }
                    _usedInputs.Add(input);
                    // A cleared value is left out, so the call stays invalid (and the PowerFlow off) until it is set again.
                    var value = Text(Inputs[input], $"Argument '{argument}'");
                    if (value is not string { Length: 0 } || string.IsNullOrEmpty(Inputs[input]?.ToString()))
                        arguments[argument] = value;
                }
                Config["arguments"] = arguments;
            }

            private static List<object?> Mappings(List<(PblPipelineFieldValue Field, object? Value)> inputs) =>
                inputs.Select(i => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) { ["field"] = i.Field, ["value"] = i.Value }).ToList();
        }

        private FieldIndex Index => index;

        private QblFieldInfo? Field(string app, string table, string field) => index.Field(app, table, field);
    }
}
