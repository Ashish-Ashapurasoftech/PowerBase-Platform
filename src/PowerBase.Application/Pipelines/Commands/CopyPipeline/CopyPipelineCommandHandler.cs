using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Commands.CreatePipeline;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Commands.CopyPipeline;

public class CopyPipelineCommandHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly ITenantUnitOfWork _uow;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;

    public CopyPipelineCommandHandler(
        IPipelineRepository pipelineRepo,
        ITenantUnitOfWork uow,
        IAuditRepository auditRepo,
        IQueryContext queryContext)
    {
        _pipelineRepo = pipelineRepo;
        _uow = uow;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
    }

    public async Task<CreatePipelineResult> HandleAsync(CopyPipelineCommand command, CancellationToken ct = default)
    {
        // 1. Get original pipeline
        var source = await _pipelineRepo.GetByPublicIdAsync(command.SourcePipelinePublicId, ct);
        
        // 2. Get steps and connections
        var sourceSteps = await _pipelineRepo.GetStepsByPipelineIdAsync(source.Id, ct);
        var sourceConnections = await _pipelineRepo.GetConnectionsByPipelineIdAsync(source.Id, ct);

        // 3. Compute unique copied name
        var existingNames = await _pipelineRepo.GetPipelineNamesForUserAsync(_queryContext.UserId, ct);
        var baseName = $"{source.Name} - Copy";
        var newName = baseName;
        var counter = 2;

        while (existingNames.Contains(newName, StringComparer.OrdinalIgnoreCase))
        {
            newName = $"{baseName} {counter}";
            counter++;
        }

        // Keep name under 200 characters limit
        if (newName.Length > 200)
        {
            newName = newName.Substring(0, 200);
        }

        // 4. Create new pipeline structure
        var newPipeline = new Pipeline
        {
            AppId = source.AppId,
            Name = newName,
            Description = source.Description,
            VariablesJson = source.VariablesJson,
            IsActive = false, // Always start inactive
            CreatedOn = DateTime.UtcNow,
            CreatedBy = _queryContext.UserId
        };

        // Start transaction
        await _uow.BeginAsync(ct);
        try
        {
            // Save Pipeline
            var (newPublicId, newId) = await _pipelineRepo.CreateAsync(newPipeline, _uow.Transaction, ct);

            // Duplicate connections
            foreach (var conn in sourceConnections)
            {
                var newConn = new PipelineConnection
                {
                    PipelineId = newId,
                    Name = conn.Name,
                    Type = conn.Type,
                    CredentialsJson = conn.CredentialsJson,
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = _queryContext.UserId
                };
                await _pipelineRepo.CreateConnectionAsync(newConn, _uow.Transaction, ct);
            }

            // Create step mapping tables
            var publicIdMap = new Dictionary<Guid, Guid>();
            var refIdMap = new Dictionary<string, string>();
            var random = new Random();
            var generatedRefIds = new HashSet<string>();

            foreach (var step in sourceSteps)
            {
                var newStepPublicId = Guid.NewGuid();
                publicIdMap[step.PublicId] = newStepPublicId;

                string newRefId;
                do
                {
                    newRefId = $"ref_{random.Next(1000, 10000)}";
                } while (generatedRefIds.Contains(newRefId));
                generatedRefIds.Add(newRefId);

                refIdMap[step.RefId] = newRefId;
            }

            // Map and update steps
            var newSteps = new List<PipelineStep>();
            foreach (var step in sourceSteps)
            {
                if (step.Type == "trigger" && step.Subtype == "schedule")
                {
                    continue; // Route 1: Discard the legacy Route 1 schedule trigger step during duplication
                }

                var newStep = new PipelineStep
                {
                    PublicId = publicIdMap[step.PublicId],
                    RefId = refIdMap[step.RefId],
                    PipelineId = newId,
                    ParentBranch = step.ParentBranch,
                    Label = step.Label,
                    Notes = step.Notes,
                    IsValidated = step.IsValidated,
                    DisplayOrder = step.DisplayOrder,
                    Type = step.Type,
                    Subtype = step.Subtype,
                    ConfigJson = step.ConfigJson,
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = _queryContext.UserId
                };

                Guid? parentPublicId = null;
                if (step.ParentStepId.HasValue)
                {
                    var parentStep = sourceSteps.FirstOrDefault(s => s.Id == step.ParentStepId.Value);
                    if (parentStep != null)
                    {
                        parentPublicId = parentStep.PublicId;
                    }
                }
                else if (step.ParentPublicId.HasValue)
                {
                    parentPublicId = step.ParentPublicId.Value;
                }

                if (parentPublicId.HasValue && publicIdMap.TryGetValue(parentPublicId.Value, out var newParentPublicId))
                {
                    newStep.ParentPublicId = newParentPublicId;
                }

                // Remap ConfigJson properties
                if (!string.IsNullOrEmpty(newStep.ConfigJson))
                {
                    try
                    {
                        var node = JsonNode.Parse(newStep.ConfigJson);
                        RemapJsonNode(node, publicIdMap, refIdMap);
                        newStep.ConfigJson = node?.ToJsonString();
                    }
                    catch
                    {
                        // Fallback in case of parse issues, keep original
                    }
                }

                newSteps.Add(newStep);
            }

            // Get row version of newly created pipeline
            var rowVersion = await _pipelineRepo.GetRowVersionAsync(newId, _uow.Transaction, ct);

            // Save steps
            await _pipelineRepo.SaveStepsAsync(newId, newSteps, rowVersion, deactivate: false, _uow.Transaction, ct);

            // Duplicate schedule if present (Route 2)
            var sourceSchedule = await _pipelineRepo.GetScheduleByPipelineIdAsync(source.Id, ct);
            if (sourceSchedule != null)
            {
                var newSchedule = new PipelineSchedule
                {
                    PipelineId = newId,
                    ScheduleType = sourceSchedule.ScheduleType,
                    Interval = sourceSchedule.Interval,
                    TimeOfDay = sourceSchedule.TimeOfDay,
                    Weekdays = sourceSchedule.Weekdays,
                    MonthDay = sourceSchedule.MonthDay,
                    MonthOfYear = sourceSchedule.MonthOfYear,
                    RelativeWeek = sourceSchedule.RelativeWeek,
                    RelativeDay = sourceSchedule.RelativeDay,
                    TimeZone = sourceSchedule.TimeZone,
                    CronExpression = sourceSchedule.CronExpression,
                    NextRunOn = null, // Set NextRunOn to null; recalculated on manual ON
                    LastRunOn = null
                };
                await _pipelineRepo.CreateScheduleAsync(newSchedule, _uow.Transaction, ct);
            }

            await _uow.CommitAsync(ct);

            // Audit
            await _auditRepo.LogActivityAsync(
                AuditActions.Created,
                AuditEntityTypes.Pipeline,
                newPublicId.ToString(),
                $"Pipeline workflow copied: {newName} (from {source.Name})",
                appId: source.AppId,
                ct: ct);

            return new CreatePipelineResult(newPublicId, newName, newPipeline.Description, newPipeline.IsActive, newPipeline.CreatedOn);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }
    }

    private static void RemapJsonNode(JsonNode? node, Dictionary<Guid, Guid> publicIdMap, Dictionary<string, string> refIdMap)
    {
        if (node == null) return;

        if (node is JsonObject obj)
        {
            var keys = obj.Select(kvp => kvp.Key).ToList();
            foreach (var key in keys)
            {
                var child = obj[key];
                if (child is JsonValue val)
                {
                    string? strValue = null;
                    if (val.TryGetValue<string>(out var s))
                    {
                        strValue = s;
                    }
                    else
                    {
                        try
                        {
                            strValue = val.ToString();
                        }
                        catch { }
                    }

                    if (strValue != null)
                    {
                        var newValue = RemapStringValue(strValue, publicIdMap, refIdMap);
                        if (newValue != strValue)
                        {
                            child = JsonValue.Create(newValue);
                            obj[key] = child;
                        }
                    }
                }
                else
                {
                    RemapJsonNode(child, publicIdMap, refIdMap);
                }

                // Remap the object key (e.g. columnMappings keys containing placeholders)
                var newKey = RemapStringValue(key, publicIdMap, refIdMap);
                if (newKey != key)
                {
                    obj.Remove(key);
                    
                    // Collision safety: check if the new key is already present.
                    // If it is not present, add it. If it is already present, safely update it.
                    obj[newKey] = child;
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var child = arr[i];
                if (child is JsonValue val)
                {
                    string? strValue = null;
                    if (val.TryGetValue<string>(out var s))
                    {
                        strValue = s;
                    }
                    else
                    {
                        try
                        {
                            strValue = val.ToString();
                        }
                        catch { }
                    }

                    if (strValue != null)
                    {
                        var newValue = RemapStringValue(strValue, publicIdMap, refIdMap);
                        if (newValue != strValue)
                        {
                            arr[i] = JsonValue.Create(newValue);
                        }
                    }
                }
                else
                {
                    RemapJsonNode(child, publicIdMap, refIdMap);
                }
            }
        }
    }

    private static string RemapStringValue(string val, Dictionary<Guid, Guid> publicIdMap, Dictionary<string, string> refIdMap)
    {
        if (string.IsNullOrEmpty(val)) return val;

        var result = val;

        // 1. Remap all publicIds (GUIDs) embedded in the string
        foreach (var kvp in publicIdMap)
        {
            result = result.Replace(kvp.Key.ToString(), kvp.Value.ToString());
        }

        // 2. Remap all refIds (preceding step references) with word boundaries
        foreach (var kvp in refIdMap)
        {
            var pattern = $@"\b{Regex.Escape(kvp.Key)}\b";
            result = Regex.Replace(result, pattern, kvp.Value);
        }

        return result;
    }
}
