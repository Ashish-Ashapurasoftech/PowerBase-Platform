using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Forms.Commands.CreateForm;

namespace PowerBase.Application.Forms.Queries.ListForms;

public class ListFormsQueryHandler
{
    private readonly IFormRepository _formRepo;

    public ListFormsQueryHandler(IFormRepository formRepo) => _formRepo = formRepo;

    public async Task<IReadOnlyList<FormDetail>> HandleAsync(ListFormsQuery query, CancellationToken ct = default)
    {
        var forms = await _formRepo.ListByTableAsync(query.TablePublicId, ct);
        return forms.Select(CreateFormCommandHandler.MapToDetail).ToList();
    }
}
