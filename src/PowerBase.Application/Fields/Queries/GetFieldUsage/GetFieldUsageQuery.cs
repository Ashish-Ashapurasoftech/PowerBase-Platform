// MediatR not used in this project
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Application.Fields.Queries.GetFieldUsage;

public record GetFieldUsageQuery(Guid TablePublicId, Guid FieldPublicId);

public class GetFieldUsageQueryHandler
{
    private readonly IAppTableRepository _appTableRepository;
    private readonly IAppFieldRepository _appFieldRepository;

    public GetFieldUsageQueryHandler(
        IAppTableRepository appTableRepository,
        IAppFieldRepository appFieldRepository)
    {
        _appTableRepository = appTableRepository;
        _appFieldRepository = appFieldRepository;
    }

    public async Task<FieldUsageDto> HandleAsync(GetFieldUsageQuery request, CancellationToken cancellationToken = default)
    {
        var table = await _appTableRepository.GetByPublicIdAsync(request.TablePublicId, cancellationToken);
        if (table == null)
            throw new NotFoundException(nameof(table), request.TablePublicId);

        var field = await _appFieldRepository.GetByPublicIdAsync(request.FieldPublicId, cancellationToken);
        if (field == null)
            throw new NotFoundException(nameof(field), request.FieldPublicId);

        return await _appFieldRepository.GetFieldUsageAsync(table.Id, field.Id, field.Fid.GetValueOrDefault(), table.AppId, cancellationToken);
    }
}
