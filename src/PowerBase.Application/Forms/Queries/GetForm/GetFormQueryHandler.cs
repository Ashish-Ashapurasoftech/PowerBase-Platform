using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms.Commands.CreateForm;

namespace PowerBase.Application.Forms.Queries.GetForm;

public class GetFormQueryHandler
{
    private readonly IFormRepository _formRepo;

    public GetFormQueryHandler(IFormRepository formRepo) => _formRepo = formRepo;

    public async Task<FormDetail> HandleAsync(GetFormQuery query, CancellationToken ct = default)
    {
        var form = await _formRepo.GetByPublicIdAsync(query.FormPublicId, ct);
        return CreateFormCommandHandler.MapToDetail(form);
    }
}
