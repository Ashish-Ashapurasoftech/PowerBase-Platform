using FluentValidation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PowerBase.Application.Pipelines.Commands.SavePipelineSteps;

public class SavePipelineStepsCommandValidator : AbstractValidator<SavePipelineStepsCommand>
{
    public SavePipelineStepsCommandValidator()
    {
        RuleFor(x => x.PipelinePublicId).NotEmpty();
        RuleFor(x => x.Steps).NotNull();
        RuleFor(x => x).Custom(ValidateSteps);
    }

    private void ValidateSteps(SavePipelineStepsCommand command, ValidationContext<SavePipelineStepsCommand> context)
    {
        var steps = command.Steps;
        if (steps == null || steps.Count == 0)
        {
            context.AddFailure("Steps", "Pipeline must contain at least one step.");
            return;
        }

        // Rule 1: Must begin with exactly one Trigger or Search/Query step at root index 0
        var firstStep = steps[0];
        bool isValidFirstStep = firstStep.Type == "trigger" || (firstStep.Type == "query" && (firstStep.Subtype == "search-records" || firstStep.Subtype == "look-up-record"));
        if (!isValidFirstStep)
        {
            context.AddFailure("Steps", "A pipeline must begin with either a Trigger step or a Search/Query step.");
        }

        var stepById = new Dictionary<string, SavePipelineStepDto>();
        var triggerCount = 0;

        // Traverse steps hierarchically to build lists and check structure
        var allStepsFlat = new List<(SavePipelineStepDto Step, string? ParentRefId, string? BranchType)>();
        var parentMap = new Dictionary<string, string>(); // childRefId -> parentRefId

        void Traverse(List<SavePipelineStepDto> list, string? parentRefId, string? branchType)
        {
            if (list == null) return;
            foreach (var step in list)
            {
                allStepsFlat.Add((step, parentRefId, branchType));
                if (!string.IsNullOrEmpty(step.RefId))
                {
                    stepById[step.RefId] = step;
                    if (!string.IsNullOrEmpty(parentRefId))
                    {
                        parentMap[step.RefId] = parentRefId;
                    }
                }

                if (step.Type == "trigger")
                {
                    triggerCount++;
                }

                // Check nested collections
                if (step.Children != null) Traverse(step.Children, step.RefId, "children");
                if (step.ElseChildren != null) Traverse(step.ElseChildren, step.RefId, "elseChildren");
                if (step.SuccessChildren != null) Traverse(step.SuccessChildren, step.RefId, "successChildren");
                if (step.ErrorChildren != null) Traverse(step.ErrorChildren, step.RefId, "errorChildren");
            }
        }

        Traverse(steps, null, null);

        // Rule 2 & 3: Only one trigger is allowed in a pipeline, and no nested triggers
        if (triggerCount > 1)
        {
            context.AddFailure("Steps", "Multiple triggers are forbidden in a single pipeline.");
        }

        bool IsAncestor(string childRefId, string ancestorRefId)
        {
            var curr = childRefId;
            while (parentMap.TryGetValue(curr, out var parent))
            {
                if (parent == ancestorRefId) return true;
                curr = parent;
            }
            return false;
        }

        List<string> GetAncestorPath(string stepRefId)
        {
            var path = new List<string>();
            var curr = stepRefId;
            while (parentMap.TryGetValue(curr, out var parent))
            {
                path.Add(parent);
                curr = parent;
            }
            path.Reverse();
            return path;
        }

        bool IsPrefix(List<string> prefixList, List<string> fullList)
        {
            if (prefixList.Count > fullList.Count) return false;
            for (int i = 0; i < prefixList.Count; i++)
            {
                if (prefixList[i] != fullList[i]) return false;
            }
            return true;
        }

        bool IsValidFieldRef(string val)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(val, @"^fid_(?:\d+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$") || Guid.TryParse(val, out _);
        }

        var traversedRefIds = new HashSet<string>();
        foreach (var (step, parentRefId, branchType) in allStepsFlat)
        {
            // Rule 3 (Nested triggers check): If trigger, it must be at the root (parentRefId == null)
            if (step.Type == "trigger" && parentRefId != null)
            {
                context.AddFailure("Steps", $"Trigger step '{step.RefId}' cannot be nested inside container steps.");
            }

            // Rule 4 (Stop constraint): Stop step is only enabled/valid if nested inside a Condition or Loop or Error Handler
            if (step.Subtype == "stop" && parentRefId == null)
            {
                context.AddFailure("Steps", $"Stop step '{step.RefId}' must be nested inside a Condition or Loop branch.");
            }

            // Rule 5 (Loop target check): Loop must iterate over preceding steps that exist and return collections
            if (step.Type == "loop")
            {
                string? loopOverStepId = null;
                if (!string.IsNullOrEmpty(step.ConfigJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(step.ConfigJson);
                        if (doc.RootElement.TryGetProperty("loopOverStepId", out var prop))
                        {
                            loopOverStepId = prop.GetString();
                        }
                    }
                    catch { }
                }

                if (step.IsValidated && string.IsNullOrEmpty(loopOverStepId))
                {
                    context.AddFailure("Steps", $"Loop step '{step.RefId}' requires a valid target step selection (loopOverStepId).");
                }
                else if (!string.IsNullOrEmpty(loopOverStepId))
                {
                    if (!traversedRefIds.Contains(loopOverStepId))
                    {
                        context.AddFailure("Steps", $"Loop step '{step.RefId}' refers to a step '{loopOverStepId}' that is not preceding or does not exist.");
                    }
                    else if (stepById.TryGetValue(loopOverStepId, out var targetStep))
                    {
                        var isListProvider = targetStep.Type == "query" ||
                                             targetStep.Subtype == "bulk" ||
                                             targetStep.Subtype == "new-bulk-event" ||
                                             targetStep.Subtype == "search-records" ||
                                             targetStep.Subtype == "export-records-csv";
                        if (!isListProvider)
                        {
                            context.AddFailure("Steps", $"Loop step '{step.RefId}' cannot iterate over step '{loopOverStepId}' because it does not return a collection of records.");
                        }
                    }
                }
            }

            if (step.Subtype == "update-record" || step.Subtype == "delete-record")
            {
                string? targetRecordId = null;
                if (!string.IsNullOrEmpty(step.ConfigJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(step.ConfigJson);
                        if (doc.RootElement.TryGetProperty("targetRecordId", out var prop))
                        {
                            targetRecordId = prop.GetString();
                        }
                    }
                    catch { }
                }

                if (step.IsValidated && string.IsNullOrEmpty(targetRecordId))
                {
                    context.AddFailure("Steps", $"Step '{step.RefId}' requires a valid target record selection (targetRecordId).");
                }
                else if (!string.IsNullOrEmpty(targetRecordId) && !Guid.TryParse(targetRecordId, out _))
                {
                    var targetRef = targetRecordId;
                    if (targetRef.StartsWith("{{") && targetRef.EndsWith("}}"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(targetRef, @"\{\{\s*(?:steps\.)?([a-zA-Z0-9_]+)");
                        if (match.Success)
                        {
                            targetRef = match.Groups[1].Value;
                        }
                    }

                    if (targetRef == "trigger")
                    {
                        var triggerStep = steps[0];
                        if (triggerStep.Subtype != "record-added" && triggerStep.Subtype != "record-updated" && triggerStep.Subtype != "new-event")
                        {
                            context.AddFailure("Steps", $"Step '{step.RefId}' cannot target trigger '{targetRef}' because the trigger event does not return a single record.");
                        }
                    }
                    else if (!traversedRefIds.Contains(targetRef))
                    {
                        context.AddFailure("Steps", $"Step '{step.RefId}' refers to target record step '{targetRef}' that is not preceding or does not exist.");
                    }
                    else if (stepById.TryGetValue(targetRef, out var targetStep))
                    {
                        var isSingleRecordProvider = targetStep.Subtype == "create-record" ||
                                                     targetStep.Subtype == "update-record" ||
                                                     targetStep.Type == "loop" ||
                                                     targetStep.Subtype == "look-up-record" ||
                                                     targetStep.Subtype == "new-event";
                        if (!isSingleRecordProvider)
                        {
                            context.AddFailure("Steps", $"Step '{step.RefId}' cannot target step '{targetRef}' because it does not return a single record.");
                        }
                    }
                }
            }

            if (step.Subtype == "search-records")
            {
                string? tableId = null;
                if (!string.IsNullOrEmpty(step.ConfigJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(step.ConfigJson);
                        if (doc.RootElement.TryGetProperty("tableId", out var prop))
                        {
                            tableId = prop.GetString();
                        }
                    }
                    catch { }
                }

                if (step.IsValidated && (string.IsNullOrEmpty(tableId) || !Guid.TryParse(tableId, out _)))
                {
                    context.AddFailure("Steps", $"Search Records step '{step.RefId}' requires a valid table selection.");
                }
            }

            if (step.Subtype == "look-up-record")
            {
                string? tablePublicId = null;
                string? recordIdValue = null;
                if (!string.IsNullOrEmpty(step.ConfigJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(step.ConfigJson);
                        if (doc.RootElement.TryGetProperty("tablePublicId", out var prop))
                        {
                            tablePublicId = prop.GetString();
                        }
                        if (doc.RootElement.TryGetProperty("recordIdValue", out var propVal))
                        {
                            recordIdValue = propVal.GetString();
                        }
                    }
                    catch { }
                }

                if (step.IsValidated)
                {
                    if (string.IsNullOrEmpty(tablePublicId) || !Guid.TryParse(tablePublicId, out _))
                    {
                        context.AddFailure("Steps", $"Look Up a Record step '{step.RefId}' requires a valid table selection.");
                    }
                    if (string.IsNullOrEmpty(recordIdValue))
                    {
                        context.AddFailure("Steps", $"Look Up a Record step '{step.RefId}' requires a valid lookup value.");
                    }
                }
            }

            // Variable Scope Validation
            if (!string.IsNullOrEmpty(step.ConfigJson))
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(step.ConfigJson, @"\{\{\s*(?:steps\.)?([a-zA-Z0-9_]+)(?:\.[a-zA-Z0-9_\[\]\.#]+)?\s*\}\}");
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var refId = match.Groups[1].Value;
                    if (refId == "trigger" || refId.StartsWith("fid_")) continue;

                    if (!traversedRefIds.Contains(refId))
                    {
                        context.AddFailure("Steps", $"Step '{step.RefId}' refers to a step '{refId}' in placeholder '{match.Value}' that does not exist or is not preceding.");
                    }
                    else if (stepById.TryGetValue(refId, out var refStep))
                    {
                        // Check container prefix scoping rule for step outputs
                        var ancestorsX = GetAncestorPath(refId);
                        var ancestorsY = GetAncestorPath(step.RefId);
                        if (!IsPrefix(ancestorsX, ancestorsY))
                        {
                            context.AddFailure("Steps", $"Step '{step.RefId}' cannot reference step '{refId}' because the target step is inside an inaccessible container.");
                        }

                        // Check loop variables specific ancestor rule
                        if (refStep.Type == "loop")
                        {
                            if (!IsAncestor(step.RefId, refId))
                            {
                                context.AddFailure("Steps", $"Step '{step.RefId}' cannot reference loop step '{refId}' outside of its loop container.");
                            }
                        }
                    }
                }

                // Stable FID Mapping Validation
                if (step.IsValidated)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(step.ConfigJson);
                        var root = doc.RootElement;

                        string[] arrayKeys = { "fields", "sourceFields", "destinationFields", "subsequentFields", "selectedFileFields", "monitoredFields", "triggerFields" };
                        foreach (var key in arrayKeys)
                        {
                            if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in prop.EnumerateArray())
                                {
                                    var val = item.GetString();
                                    if (val != null && !string.IsNullOrWhiteSpace(val) && !IsValidFieldRef(val))
                                    {
                                        context.AddFailure("Steps", $"Step '{step.RefId}' configuration field '{key}' contains invalid visual label reference: '{val}'. It must use stable FID.");
                                    }
                                }
                            }
                        }

                        if (root.TryGetProperty("fieldMappings", out var mappingsProp) && mappingsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in mappingsProp.EnumerateArray())
                            {
                                if (item.TryGetProperty("field", out var fProp))
                                {
                                    var val = fProp.GetString();
                                    if (val != null && !string.IsNullOrWhiteSpace(val) && !IsValidFieldRef(val))
                                    {
                                        context.AddFailure("Steps", $"Step '{step.RefId}' field mapping contains invalid visual label reference: '{val}'. It must use stable FID.");
                                    }
                                }
                            }
                        }

                        string[] dictKeys = { "fieldValues", "rowValues", "columnMappings" };
                        foreach (var key in dictKeys)
                        {
                            if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var objProp in prop.EnumerateObject())
                                {
                                    if (key == "columnMappings")
                                    {
                                        var val = objProp.Value.GetString();
                                        if (val != null && !string.IsNullOrWhiteSpace(val) && !IsValidFieldRef(val))
                                        {
                                            context.AddFailure("Steps", $"Step '{step.RefId}' configuration dictionary '{key}' contains invalid visual label value reference: '{val}'. It must use stable FID.");
                                        }
                                    }
                                    else
                                    {
                                        var val = objProp.Name;
                                        if (!string.IsNullOrWhiteSpace(val) && !IsValidFieldRef(val))
                                        {
                                            context.AddFailure("Steps", $"Step '{step.RefId}' configuration dictionary '{key}' contains invalid visual label key reference: '{val}'. It must use stable FID.");
                                        }
                                    }
                                }
                            }
                        }

                        if (root.TryGetProperty("mergeField", out var mergeProp))
                        {
                            var val = mergeProp.GetString();
                            if (val != null && !string.IsNullOrWhiteSpace(val) && !IsValidFieldRef(val))
                            {
                                context.AddFailure("Steps", $"Step '{step.RefId}' merge field contains invalid visual label reference: '{val}'. It must use stable FID.");
                            }
                        }
                    }
                    catch { }
                }
            }

            if (!string.IsNullOrEmpty(step.RefId))
            {
                traversedRefIds.Add(step.RefId);
            }
        }
    }
}

