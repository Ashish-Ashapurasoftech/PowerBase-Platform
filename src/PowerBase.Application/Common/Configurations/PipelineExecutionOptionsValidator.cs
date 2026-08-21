using System;

namespace PowerBase.Application.Common.Configurations;

public static class PipelineExecutionOptionsValidator
{
    public static void Validate(PipelineExecutionOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // Unconditional Database Queue validation

        if (options.PerInstanceTenantConcurrencyLimit <= 0)
        {
            throw new InvalidOperationException("PipelineExecution:PerInstanceTenantConcurrencyLimit must be greater than zero.");
        }

        if (options.DatabaseQueue == null)
        {
            throw new InvalidOperationException("PipelineExecution:DatabaseQueue options are missing.");
        }

        if (options.DatabaseQueue.LeaseSeconds <= 0)
        {
            throw new InvalidOperationException("PipelineExecution:DatabaseQueue:LeaseSeconds must be greater than zero.");
        }

        if (options.DatabaseQueue.MaxAttempts <= 0)
        {
            throw new InvalidOperationException("PipelineExecution:DatabaseQueue:MaxAttempts must be greater than zero.");
        }
    }
}
