using System;
using System.Collections.Generic;

namespace PowerBase.Application.Pipelines.Queries.GetPipelineRunSteps;

public record GetPipelineRunStepsQuery(
    Guid RunPublicId
);

public record PipelineStepRunDto(
    long Id,
    Guid StepPublicId,
    string StepRefId,
    string StepLabel,
    string StepType,
    string StepSubtype,
    string Status,
    DateTime StartedOn,
    DateTime? CompletedOn,
    string? InputContext,
    string? OutputContext,
    string? LogMessage
);
