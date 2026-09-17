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

        var firstExecutableStep = GetFirstExecutableStep(root, activeSteps);
        return firstExecutableStep != null &&
               (string.Equals(firstExecutableStep.Type, "action", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(firstExecutableStep.Type, "query", StringComparison.OrdinalIgnoreCase));
    }

    private static PipelineStep? GetFirstExecutableStep(PipelineStep current, List<PipelineStep> activeSteps)
    {
        if (current.Subtype == "handle-errors")
        {
            var firstChild = activeSteps
                .Where(s => s.ParentStepId == current.Id && string.Equals(s.ParentBranch, "children", StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.DisplayOrder)
                .ThenBy(s => s.Id)
                .FirstOrDefault();

            if (firstChild == null) return null;
            return GetFirstExecutableStep(firstChild, activeSteps);
        }

        return current;
    }
}
