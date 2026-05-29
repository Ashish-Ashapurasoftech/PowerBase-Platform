using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Forms.Commands.SetDefaultForm;

public record SetDefaultFormCommand(Guid TableId, Guid FormId);

public class SetDefaultFormCommandHandler
{
    private readonly IFormRepository _formRepo;

    public SetDefaultFormCommandHandler(IFormRepository formRepo)
    {
        _formRepo = formRepo;
    }

    public async Task HandleAsync(SetDefaultFormCommand request, CancellationToken ct)
    {
        await _formRepo.SetDefaultAsync(request.TableId, request.FormId, ct);
    }
}
