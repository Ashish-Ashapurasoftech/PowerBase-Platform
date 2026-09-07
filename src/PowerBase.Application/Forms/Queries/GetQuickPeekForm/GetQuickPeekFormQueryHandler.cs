using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms.Commands.CreateForm;

namespace PowerBase.Application.Forms.Queries.GetQuickPeekForm;

public class GetQuickPeekFormQueryHandler
{
    private readonly IFormRepository _formRepo;

    public GetQuickPeekFormQueryHandler(IFormRepository formRepo) => _formRepo = formRepo;

    /// <summary>Null when the table has no form flagged as its Quick Peek form.</summary>
    public async Task<FormDetail?> HandleAsync(GetQuickPeekFormQuery query, CancellationToken ct = default)
    {
        var form = await _formRepo.GetQuickPeekFormAsync(query.TableId, ct);
        return form == null ? null : CreateFormCommandHandler.MapToDetail(form);
    }
}
