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
        // Send an Email ("email") and Upload a File ("file") start a PowerFlow run by hand or on a schedule like an action does.
        return firstExecutableStep != null &&
               (string.Equals(firstExecutableStep.Type, "action", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(firstExecutableStep.Type, "query", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(firstExecutableStep.Type, "email", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(firstExecutableStep.Type, "file", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A first step a manual, activation or scheduled run can start from (the steps the save rule allows first,
    /// triggers aside): a query, these actions, Send Email, and — naming their record by Record ID — Update / Delete / Upload.</summary>
    public static bool IsRunnableRoot(PipelineStep? step) => step != null && (
        (step.Type == "query" && step.Subtype is "search-records" or "look-up-record") ||
        (step.Type == "action" && step.Subtype is "prepare-bulk-upsert" or "copy-records" or "make-request" or "pause" or "import-with-csv"
            or "create-record" or "update-record" or "delete-record" or "call-another-pipeline") ||
        (step.Type == "email" && step.Subtype is "send-email" or "send-email-outlook") ||
        (step.Type == "file" && step.Subtype == "upload-file"));

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
