using PowerBase.Application.Pipelines.Commands.UpdatePipeline;
using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Pipelines.Commands.CreatePipeline;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Services;

/// <summary>
/// Quickbase pipelines → PowerFlows, once every application of the file exists. Each pipeline is created in its application
/// through the PowerFlows handlers a user goes through (<see cref="CreatePipelineCommandHandler"/>, then
/// <see cref="SavePipelineStepsCommandHandler"/> with all of its validation), so an imported PowerFlow is indistinguishable from
/// one built by hand. Imported PowerFlows are always off, as Quickbase imports them; a step that is incomplete is saved as a
/// draft, which keeps the PowerFlow from being turned on until it is fixed. Failures are reported per pipeline and never fail
/// the import.
/// </summary>
public class PblPipelinesImporter(
    CreatePipelineCommandHandler createPipelineHandler,
    SavePipelineStepsCommandHandler saveStepsHandler,
    IPipelineRepository pipelineRepo,
    IAppRepository appRepo,
    IAppTableRepository tableRepo,
    IAppFieldRepository fieldRepo,
    ITenantRepository tenantRepo,
    IQueryContext queryContext,
    UpdatePipelineCommandHandler? updatePipelineHandler = null)
{
    private const int NAME_MAX = 200;
    private const int DESCRIPTION_MAX = 500;

    private IAppRepository AppRepo => appRepo;
    private IAppTableRepository TableRepo => tableRepo;
    private IAppFieldRepository FieldRepo => fieldRepo;
    private ITenantRepository TenantRepo => tenantRepo;
    private IQueryContext QueryContext => queryContext;

    /// <summary>Imports the pipelines of <paramref name="apps"/> into the applications that were created
    /// (<paramref name="imported"/>). A pipeline whose application was not created is reported on the first one that was.</summary>
    public virtual async Task ImportAsync(IReadOnlyList<ParsedApp> apps, IReadOnlyList<(ParsedApp App, ImportReport Report)> imported, CancellationToken ct, bool activate = false)
    {
        var pipelines = apps.SelectMany(a => a.Pipelines ?? []).ToList();
        if (pipelines.Count == 0 || imported.Count == 0)
            return;

        var resolver = new Resolver(this, imported);
        foreach (var pipeline in pipelines)
        {
            var home = imported.FirstOrDefault(i => string.Equals(i.App.AppRef, pipeline.AppRef, StringComparison.Ordinal));
            var report = home.Report ?? imported[0].Report;
            var item = NewItem(pipeline);
            report.Pipelines.Add(item);
            try
            {
                if (pipeline.IsUnreadable)
                    continue;
                if (home.Report is null)
                {
                    Issue(item, "PIPELINE_APP_NOT_IMPORTED", pipeline.AppRef is null
                        ? "The pipeline does not say which application it belongs to; it was not imported."
                        : "The application this pipeline belongs to was not imported, so neither was the pipeline.");
                    continue;
                }
                await ImportOneAsync(pipeline, home.Report, resolver, item, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.NeedsReview = true;
                Issue(item, "PIPELINE_IMPORT_FAILED", $"The PowerFlow could not be imported completely ({Describe(ex)}).");
            }
            finally
            {
                item.NeedsReview |= item.Status != PipelineImportStatuses.Created || pipeline.ChangesExecution;
            }
            if (activate && item.PipelinePublicId is { } publicId)
                await ActivateAsync(publicId, item, ct);
        }
    }

    private static PipelineImportReportItem NewItem(PblPipeline pipeline)
    {
        var item = new PipelineImportReportItem
        {
            LogicalRef = pipeline.LogicalRef,
            Name = pipeline.Name,
            SourceOwner = pipeline.Owner,
            StepsSkipped = pipeline.SkippedStepCount,
        };
        item.Issues.AddRange(pipeline.Issues.Select(i => new PipelineImportIssueItem
        {
            Code = i.Code, Message = i.Message, StepId = i.StepId, StepName = i.StepName, IsSkippedStep = i.IsSkippedStep,
        }));
        return item;
    }

    private async Task ImportOneAsync(PblPipeline pipeline, ImportReport report, Resolver resolver, PipelineImportReportItem item, CancellationToken ct)
    {
        var steps = new List<SavePipelineStepDto>();
        for (var i = 0; i < pipeline.Steps.Count; i++)
            steps.Add(await ToDtoAsync(pipeline.Steps[i], i, resolver, item, ct));
        NumberSteps(steps, 1);

        var name = await UniqueNameAsync(pipeline.Name, ct);
        if (!string.Equals(name, pipeline.Name, StringComparison.Ordinal))
            Issue(item, "PIPELINE_RENAMED", $"Imported as '{name}': you already have a PowerFlow named '{pipeline.Name}'.");
        string? description = pipeline.Description;
        if (description?.Length > DESCRIPTION_MAX)
        {
            Issue(item, "PIPELINE_DESCRIPTION_NOT_IMPORTED", $"The description is longer than {DESCRIPTION_MAX} characters and was not imported.");
            description = null;
        }

        var created = await createPipelineHandler.HandleAsync(new CreatePipelineCommand(report.AppPublicId, name, description), ct);
        item.Name = created.Name;
        item.PipelinePublicId = created.PublicId;
        item.Status = PipelineImportStatuses.Created;

        if (steps.Count > 0)
            await SaveStepsAsync(created.PublicId, steps, item, ct);
        else if (pipeline.SourceStepCount > 0)
            item.Status = PipelineImportStatuses.CreatedWithoutSteps;

        if (item.Status == PipelineImportStatuses.Created)
        {
            item.StepsImported = Count(steps);
            // A draft step keeps the PowerFlow from being turned on; the report says why.
            item.NeedsReview |= Flatten(steps).Any(s => !s.IsValidated);
        }

        await ImportScheduleAsync(pipeline, created.PublicId, item, ct);
    }

    private async Task SaveStepsAsync(Guid pipelinePublicId, List<SavePipelineStepDto> steps, PipelineImportReportItem item, CancellationToken ct)
    {
        List<SavePipelineStepDto> dropped = [];
        DomainException? triggerFailure = null;
        while (true)
        {
            try
            {
                if (steps.Count == 0)
                    throw triggerFailure!;
                // Saved the way the editor leaves each step, so opening the PowerFlow saves nothing (and never turns it off).
                PblPipelineEditorParity.Apply(steps);
                var pipeline = await pipelineRepo.GetByPublicIdAsync(pipelinePublicId, ct);
                await saveStepsHandler.HandleAsync(new SavePipelineStepsCommand(pipelinePublicId, steps, pipeline.RowVersion), ct);
                foreach (var trigger in dropped)
                    item.Issues.Add(new PipelineImportIssueItem
                    {
                        Code = "PIPELINE_TRIGGER_NOT_SAVED",
                        Message = $"The trigger '{trigger.Label}' was left out: its table is not in the file, so PowerFlows cannot save it ({Describe(triggerFailure!)}). Add the trigger by hand; the other steps were saved as drafts.",
                        StepName = trigger.Label,
                    });
                return;
            }
            catch (DomainException first) when (Flatten(steps).Any(s => s.IsValidated))
            {
                // Validated steps also get the database-backed checks; one that fails there is saved as a draft, like the editor does.
                Issue(item, "PIPELINE_STEP_INCOMPLETE", $"Some steps did not pass PowerFlows validation and were saved as drafts ({Describe(first)}).");
                foreach (var step in Flatten(steps))
                    step.IsValidated = false;
            }
            catch (DomainException failed) when (steps.Any(IsUnboundTrigger))
            {
                // The save checks a record trigger's app and table even for a draft, so one whose table is in another
                // application (not in the file) would lose every step. Leave the trigger out and keep the rest as drafts.
                triggerFailure = failed;
                foreach (var trigger in steps.Where(IsUnboundTrigger).ToList())
                {
                    dropped.Add(trigger);
                    steps.Remove(trigger);
                }
                for (var i = 0; i < steps.Count; i++)
                    steps[i].DisplayOrder = i;
                NumberSteps(steps, 1);
            }
            catch (DomainException failed)
            {
                // Also when the steps still cannot be saved without the trigger (e.g. the next step cannot start a PowerFlow).
                NotSaved(item, failed);
                return;
            }
        }
    }

    /// <summary>A record trigger with no app or table, which <see cref="PipelineStepValidator.ValidateNewEventStepAsync"/> rejects
    /// even as a draft.</summary>
    private static bool IsUnboundTrigger(SavePipelineStepDto step)
    {
        if (step.Type != "trigger" || step.Subtype is not ("new-event" or "new-bulk-event"))
            return false;
        try
        {
            var config = JsonDocument.Parse(step.ConfigJson ?? "{}").RootElement;
            bool Bound(string name) => config.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                                      && Guid.TryParse(value.GetString(), out _);
            return !Bound("appPublicId") || !Bound("tablePublicId");
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static void NotSaved(PipelineImportReportItem item, DomainException failed)
    {
        item.Status = PipelineImportStatuses.CreatedWithoutSteps;
        Issue(item, "PIPELINE_STEPS_NOT_SAVED", $"The PowerFlow was created, but its steps could not be saved ({Describe(failed)}). Add them by hand.");
    }

    /// <summary>A Quickbase schedule becomes the PowerFlow's schedule, created the way <c>CopyPipelineCommandHandler</c> copies one:
    /// stored without a next run, so nothing is scheduled until the PowerFlow is turned on.</summary>
    private async Task ImportScheduleAsync(PblPipeline pipeline, Guid pipelinePublicId, PipelineImportReportItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pipeline.Crontab))
            return;
        if (item.Status != PipelineImportStatuses.Created)
        {
            Issue(item, "PIPELINE_SCHEDULE_NOT_IMPORTED", $"The schedule '{pipeline.Crontab}' was not imported because the steps were not saved.");
            return;
        }

        var timeZone = string.IsNullOrWhiteSpace(pipeline.CrontabTimezone) ? null : pipeline.CrontabTimezone.Trim();
        var command = new UpdatePipelineScheduleCommand(pipelinePublicId, "custom", null, null, null, null, null, null, null, timeZone ?? string.Empty, pipeline.Crontab.Trim());
        var validation = new UpdatePipelineScheduleCommandValidator().Validate(command);
        if (!validation.IsValid)
        {
            Issue(item, "PIPELINE_SCHEDULE_NOT_IMPORTED", $"The schedule '{pipeline.Crontab}' ({timeZone ?? "no time zone"}) was not imported: {string.Join(" ", validation.Errors.Select(e => e.ErrorMessage).Distinct())}");
            return;
        }

        var pipelineId = await pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, ct);
        if (!PipelineScheduleEligibility.IsPipelineScheduleable(await pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, ct)))
        {
            Issue(item, "PIPELINE_SCHEDULE_NOT_IMPORTED", $"The schedule '{pipeline.Crontab}' was not imported: PowerFlows only schedules a PowerFlow that has no trigger and starts with an action or query.");
            return;
        }

        await pipelineRepo.CreateScheduleAsync(new PipelineSchedule
        {
            PipelineId = pipelineId,
            ScheduleType = command.ScheduleType,
            TimeZone = command.TimeZone,
            CronExpression = command.CronExpression,
            NextRunOn = null,
            LastRunOn = null,
        }, null, ct);
    }

    /// <summary>Turns a completely imported PowerFlow on through the editor's own activation checks; one that needs review, or
    /// fails a check, stays off and the report says why.</summary>
    private async Task ActivateAsync(Guid pipelinePublicId, PipelineImportReportItem item, CancellationToken ct)
    {
        if (item.NeedsReview)
        {
            Issue(item, "PIPELINE_NOT_ACTIVATED", "Left off: the PowerFlow needs review (see the other issues) before it can be turned on.");
            return;
        }
        if (updatePipelineHandler is null)
            return;
        try
        {
            var pipeline = await pipelineRepo.GetByPublicIdAsync(pipelinePublicId, ct);
            await updatePipelineHandler.HandleAsync(new UpdatePipelineCommand(pipelinePublicId, pipeline.Name, pipeline.Description, true, pipeline.RowVersion), ct);
            item.IsActive = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            item.NeedsReview = true;
            Issue(item, "PIPELINE_NOT_ACTIVATED", $"Left off: the PowerFlow could not be turned on ({Describe(ex)}).");
        }
    }

    private async Task<string> UniqueNameAsync(string name, CancellationToken ct)
    {
        var baseName = Fit(string.IsNullOrWhiteSpace(name) ? "New pipeline" : name.Trim(), NAME_MAX);
        if (!await pipelineRepo.NameExistsForUserAsync(queryContext.UserId, baseName, ct))
            return baseName;
        for (var n = 2; ; n++)
        {
            var suffix = $" {n}";
            var candidate = Fit(baseName, NAME_MAX - suffix.Length) + suffix;
            if (!await pipelineRepo.NameExistsForUserAsync(queryContext.UserId, candidate, ct))
                return candidate;
        }
    }

    private static string Fit(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd();

    // ---- steps -----------------------------------------------------------------------------------------------------------

    /// <summary>The step numbers the editor writes (<c>_displayNumber</c>, as its <c>assignStepNumbers</c> does): a loop's steps
    /// continue the count, a condition's or Handle Errors' branches restart at 1. Without them the editor's first save of an
    /// opened step counts as a change and turns an active PowerFlow off.</summary>
    private static int NumberSteps(List<SavePipelineStepDto>? steps, int start)
    {
        var current = start;
        foreach (var step in steps ?? [])
        {
            var config = (string.IsNullOrWhiteSpace(step.ConfigJson) ? null : System.Text.Json.Nodes.JsonNode.Parse(step.ConfigJson) as System.Text.Json.Nodes.JsonObject) ?? [];
            config["_displayNumber"] = current;
            step.ConfigJson = config.ToJsonString();
            var next = current + 1;
            if (step.Type == "loop")
                next = NumberSteps(step.Children, current + 1);
            else if (step.Type == "condition" || step.Subtype == "handle-errors")
            {
                NumberSteps(step.Children, 1);
                NumberSteps(step.ElseChildren, 1);
                NumberSteps(step.SuccessChildren, 1);
                NumberSteps(step.ErrorChildren, 1);
            }
            current = next;
        }
        return current;
    }

    private async Task<SavePipelineStepDto> ToDtoAsync(PblPipelineStep step, int order, Resolver resolver, PipelineImportReportItem item, CancellationToken ct)
    {
        var problems = new List<string>();
        var config = (Dictionary<string, object?>)(await resolver.ResolveAsync(step.Config, problems, ct) ?? new Dictionary<string, object?>());

        if (problems.Count == 0 && step.Subtype == "create-record")
            problems.AddRange(await resolver.MissingRequiredFieldsAsync(config, ct));
        if (problems.Count == 0 && step.Subtype == "copy-records")
            problems.AddRange(await resolver.CopyRecordsProblemsAsync(config, ct));

        foreach (var problem in problems.Distinct())
            item.Issues.Add(new PipelineImportIssueItem { Code = "PIPELINE_STEP_INCOMPLETE", Message = problem, StepId = step.SourceLabel, StepName = step.Name });

        var dto = new SavePipelineStepDto
        {
            RefId = step.RefId,
            Label = step.Name,
            Notes = step.Notes,
            IsValidated = step.IsComplete && problems.Count == 0,
            DisplayOrder = order,
            Type = step.Type,
            Subtype = step.Subtype,
            ConfigJson = JsonSerializer.Serialize(config),
        };
        if (step.Children.Count > 0)
        {
            dto.Children = [];
            for (var i = 0; i < step.Children.Count; i++)
                dto.Children.Add(await ToDtoAsync(step.Children[i], i, resolver, item, ct));
        }
        if (step.ElseChildren.Count > 0)
        {
            dto.ElseChildren = [];
            for (var i = 0; i < step.ElseChildren.Count; i++)
                dto.ElseChildren.Add(await ToDtoAsync(step.ElseChildren[i], i, resolver, item, ct));
        }
        if (step.SuccessChildren.Count > 0)
        {
            dto.SuccessChildren = [];
            for (var i = 0; i < step.SuccessChildren.Count; i++)
                dto.SuccessChildren.Add(await ToDtoAsync(step.SuccessChildren[i], i, resolver, item, ct));
        }
        if (step.ErrorChildren.Count > 0)
        {
            dto.ErrorChildren = [];
            for (var i = 0; i < step.ErrorChildren.Count; i++)
                dto.ErrorChildren.Add(await ToDtoAsync(step.ErrorChildren[i], i, resolver, item, ct));
        }
        return dto;
    }

    private static IEnumerable<SavePipelineStepDto> Flatten(IEnumerable<SavePipelineStepDto> steps) =>
        steps.SelectMany(s => new[] { s }.Concat(Flatten(s.Children ?? [])).Concat(Flatten(s.ElseChildren ?? []))
            .Concat(Flatten(s.SuccessChildren ?? [])).Concat(Flatten(s.ErrorChildren ?? [])));

    private static int Count(IEnumerable<SavePipelineStepDto> steps) => Flatten(steps).Count();

    private static void Issue(PipelineImportReportItem item, string code, string message) =>
        item.Issues.Add(new PipelineImportIssueItem { Code = code, Message = message });

    private static string Describe(Exception ex) => ex switch
    {
        ValidationException ve => string.Join(" ", ve.Errors.SelectMany(e => e.Value).Distinct()),
        DomainException de => de.Message,
        _ => "an unexpected error",
    };

    /// <summary>Turns the reference values of a converted step into the ids of what the import created.</summary>
    private sealed class Resolver(PblPipelinesImporter owner, IReadOnlyList<(ParsedApp App, ImportReport Report)> imported)
    {
        /// <summary>A reference that points at nothing. Dropped from lists, emptied in plain settings.</summary>
        private sealed class Missing
        {
            public static readonly Missing Value = new();
        }

        private readonly Dictionary<string, Dictionary<string, AppTable>> _tablesByApp = new(StringComparer.Ordinal);
        private readonly Dictionary<long, IReadOnlyList<AppField>> _fieldsByTable = new();
        private readonly Dictionary<Guid, AppTable> _tablesByPublicId = new();
        private string? _connection;

        public async Task<object?> ResolveAsync(object? value, List<string> problems, CancellationToken ct)
        {
            var resolved = await ResolveValueAsync(value, problems, ct);
            return resolved is Missing ? null : resolved;
        }

        private async Task<object?> ResolveValueAsync(object? value, List<string> problems, CancellationToken ct)
        {
            switch (value)
            {
                case PblPipelineConnectionValue:
                    return _connection ??= (await owner.TenantRepo.GetByIdAsync(owner.QueryContext.TenantId, ct)).PublicId.ToString();
                case PblPipelineAppValue app:
                    if (Home(app.AppRef) is { } home) return home.Report.AppPublicId.ToString();
                    problems.Add("Its application was not imported.");
                    return Missing.Value;
                case PblPipelineTableValue table:
                    if (await TableAsync(table.AppRef, table.TableRef, ct) is { } found) return found.PublicId.ToString();
                    problems.Add($"Table '{table.TableRef}' was not imported; choose the table again.");
                    return Missing.Value;
                case PblPipelineFieldValue field:
                    // One part of an Address field is written as fid_N.city.
                    if (await FidAsync(field, ct) is { } fid) return field.Part is null ? $"fid_{fid}" : $"fid_{fid}.{field.Part}";
                    problems.Add($"Field '{field.Label}' was not found in the imported app and was left out.");
                    return Missing.Value;
                case PblPipelineTextValue text:
                {
                    var builder = new System.Text.StringBuilder();
                    foreach (var part in text.Parts)
                    {
                        var piece = await ResolveValueAsync(part, problems, ct);
                        if (piece is Missing) return string.Empty;
                        builder.Append(piece);
                    }
                    return builder.ToString();
                }
                case PblPipelineFieldMapValue map:
                {
                    var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (field, entry) in map.Entries)
                    {
                        var key = await ResolveValueAsync(field, problems, ct);
                        if (key is string fieldKey)
                            result[fieldKey] = await ResolveAsync(entry, problems, ct) ?? string.Empty;
                    }
                    return result;
                }
                case Dictionary<string, object?> dictionary:
                {
                    var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (key, entry) in dictionary)
                    {
                        var resolved = await ResolveValueAsync(entry, problems, ct);
                        // A field mapping or filter rule without its field means nothing; leave the whole entry out.
                        if (resolved is Missing && key == "field")
                            return Missing.Value;
                        result[key] = resolved is Missing ? string.Empty : resolved;
                    }
                    return result;
                }
                case List<object?> list:
                {
                    var result = new List<object?>();
                    foreach (var entry in list)
                    {
                        var resolved = await ResolveValueAsync(entry, problems, ct);
                        if (resolved is not Missing) result.Add(resolved);
                    }
                    return result;
                }
                default:
                    return value;
            }
        }

        private (ParsedApp App, ImportReport Report)? Home(string appRef)
        {
            foreach (var entry in imported)
                if (string.Equals(entry.App.AppRef, appRef, StringComparison.Ordinal))
                    return entry;
            return null;
        }

        /// <summary>The created table, located the way <see cref="ImportSourceRefRecorder"/> does: by the name it was created with.</summary>
        private async Task<AppTable?> TableAsync(string appRef, string tableRef, CancellationToken ct)
        {
            if (Home(appRef) is not { } home)
                return null;
            var pblTable = (home.App.Document.Tables ?? []).FirstOrDefault(t => string.Equals(t.LogicalRef, tableRef, StringComparison.Ordinal));
            if (pblTable is null)
                return null;
            if (!_tablesByApp.TryGetValue(appRef, out var tables))
            {
                var appId = await owner.AppRepo.GetIdByPublicIdAsync(home.Report.AppPublicId, ct);
                tables = (await owner.TableRepo.ListByAppAsync(appId, ct))
                    .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                _tablesByApp[appRef] = tables;
                foreach (var table in tables.Values)
                    _tablesByPublicId[table.PublicId] = table;
            }
            return tables.TryGetValue(pblTable.Name, out var created) ? created : null;
        }

        private async Task<IReadOnlyList<AppField>> FieldsAsync(AppTable table, CancellationToken ct)
        {
            if (!_fieldsByTable.TryGetValue(table.Id, out var fields))
                _fieldsByTable[table.Id] = fields = (await owner.FieldRepo.ListByTableAsync(table.Id, ct)).Where(f => !f.IsDeleted).ToList();
            return fields;
        }

        /// <summary>The Fid of an imported field. Built-in fields are the table's own system fields; a field the converter made is
        /// found by its name (as <see cref="ImportSourceRefRecorder"/> does); a relationship-made field by the Quickbase id it kept,
        /// confirmed by its label.</summary>
        private async Task<int?> FidAsync(PblPipelineFieldValue field, CancellationToken ct)
        {
            if (await TableAsync(field.AppRef, field.TableRef, ct) is not { } table)
                return null;
            var fields = await FieldsAsync(table, ct);
            bool LabelMatches(AppField f) => string.Equals(f.Label ?? f.Name, field.Label, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(f.Name, field.Label, StringComparison.OrdinalIgnoreCase);

            if (field.IsBuiltIn)
                return (fields.FirstOrDefault(f => f.IsSystem && f.Fid == field.SourceId) ?? fields.FirstOrDefault(f => f.IsSystem && LabelMatches(f)))?.Fid;

            var home = Home(field.AppRef)!.Value;
            var pblTable = (home.App.Document.Tables ?? []).First(t => string.Equals(t.LogicalRef, field.TableRef, StringComparison.Ordinal));
            var qualified = $"{field.TableRef}::{field.FieldRef}";
            if ((pblTable.Fields ?? []).FirstOrDefault(f => string.Equals(f.LogicalRef, qualified, StringComparison.Ordinal)) is { } pblField
                && fields.FirstOrDefault(f => string.Equals(f.Name, pblField.Name, StringComparison.OrdinalIgnoreCase)) is { Fid: { } byName })
                return byName;

            if (field.SourceId is { } sourceId && fields.FirstOrDefault(f => f.Fid == sourceId) is { } byId && LabelMatches(byId))
                return byId.Fid;

            var byLabel = fields.Where(LabelMatches).ToList();
            return byLabel.Count == 1 ? byLabel[0].Fid : null;
        }

        private async Task<AppTable?> TableByIdAsync(object? publicId, CancellationToken ct)
        {
            if (!Guid.TryParse(publicId?.ToString(), out var id))
                return null;
            if (!_tablesByPublicId.TryGetValue(id, out var table))
                _tablesByPublicId[id] = table = await owner.TableRepo.GetByPublicIdAsync(id, ct);
            return table;
        }

        /// <summary>The same required-field rule <see cref="PipelineStepValidator"/> applies to a validated Create Record step, so a
        /// step that would fail it is saved as a draft instead of failing the whole save.</summary>
        public async Task<List<string>> MissingRequiredFieldsAsync(Dictionary<string, object?> config, CancellationToken ct)
        {
            if (await TableByIdAsync(config.GetValueOrDefault("tablePublicId"), ct) is not { } table)
                return [];
            var mapped = (config.GetValueOrDefault("fieldMappings") as List<object?> ?? [])
                .OfType<Dictionary<string, object?>>()
                .Where(m => !string.IsNullOrWhiteSpace(m.GetValueOrDefault("value")?.ToString()))
                .Select(m => m.GetValueOrDefault("field")?.ToString())
                // A mapped part of an Address (fid_N.city) gives that Address a value.
                .Select(f => f != null && PipelineAddressMappings.IsPartReference(f) ? f[..f.IndexOf('.')] : f)
                .ToHashSet(StringComparer.Ordinal);
            return (await FieldsAsync(table, ct))
                .Where(f => f.IsRequired && !f.IsSystem && f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode) && !mapped.Contains($"fid_{f.Fid}"))
                .Select(f => $"Required field '{(string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label)}' has no value; set it before turning the PowerFlow on.")
                .ToList();
        }

        /// <summary>The field rules of <see cref="CopyRecordsDefinition.ValidateFields"/>, checked before saving for the same reason.</summary>
        public async Task<List<string>> CopyRecordsProblemsAsync(Dictionary<string, object?> config, CancellationToken ct)
        {
            try
            {
                var definition = CopyRecordsDefinition.Read(JsonSerializer.Serialize(config));
                definition.ValidateShape();
                var source = await owner.TableRepo.GetByPublicIdAsync(CopyRecordsDefinition.TableId(definition.SourceTable), ct);
                var destination = await owner.TableRepo.GetByPublicIdAsync(CopyRecordsDefinition.TableId(definition.DestinationTable), ct);
                definition.ValidateFields(await FieldsAsync(source, ct), await FieldsAsync(destination, ct));
                return [];
            }
            catch (ValidationException ex)
            {
                return ex.Errors.SelectMany(e => e.Value).ToList();
            }
            catch (NotFoundException)
            {
                return ["A table of this step was not found."];
            }
        }
    }
}
