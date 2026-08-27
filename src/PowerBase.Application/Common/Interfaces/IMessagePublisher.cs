using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Application.Common.Interfaces;

public interface IMessagePublisher
{
    Task PublishAsync<T>(T message, CancellationToken ct = default);
    Task PublishBatchAsync<T>(IEnumerable<T> messages, CancellationToken ct = default);
}
