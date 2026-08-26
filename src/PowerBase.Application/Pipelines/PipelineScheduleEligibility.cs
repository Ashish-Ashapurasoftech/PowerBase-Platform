using System;
using System.Collections.Generic;
using System.Linq;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

public static class PipelineScheduleEligibility
{
    public static bool IsPipelineScheduleable(IEnumerable<PipelineStep> steps)
    {
        var activeSteps = steps.Where(s => !s.IsDeleted).ToList();
        if (activeSteps.Count == 0) return false;

        // Reject any trigger steps anywhere in the canvas
        if (activeSteps.Any(s => s.Type == "trigger")) return false;

        var rootSteps = activeSteps
            .Where(s => s.ParentStepId == null && s.ParentBranch == null)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .ToList();

        if (rootSteps.Count == 0) return false;

        var root = rootSteps[0];

        // Check allow-list of executable root subtypes
        var scheduleableSubtypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "search-records", "look-up-record", "create-record", "send-email", "send-email-outlook",
            "make-request", "prepare-bulk-upsert"
        };
        if (string.IsNullOrEmpty(root.Subtype) || !scheduleableSubtypes.Contains(root.Subtype)) return false;

        return true;
    }
}
