using PowerBase.Domain.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Application.Common.Interfaces;

public interface IPipelineEngine
{
    Task ExecuteAsync(PipelineExecutionTask task, CancellationToken ct);
}
