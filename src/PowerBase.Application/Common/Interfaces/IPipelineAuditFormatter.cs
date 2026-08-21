using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IPipelineAuditFormatter
{
    Task InitializeAsync(long pipelineId, long triggeredByUserId, CancellationToken ct);

    (string InputContextJson, string OutputContextJson, string LogMessage) FormatStepRun(
        PipelineStep step,
        string? rawInputJson,
        string? rawOutputJson,
        string status,
        string correlationId,
        DateTime? startedOn,
        DateTime? completedOn);
}
