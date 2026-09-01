using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Forms.Commands.SetQuickPeekForm;

/// <summary>Sets which form (if any) is used for Quick Peek across every report on this table.
/// <paramref name="FormId"/> null clears the table's Quick Peek form entirely.</summary>
public record SetQuickPeekFormCommand(Guid TableId, Guid? FormId);

public class SetQuickPeekFormCommandHandler
{
    private readonly IFormRepository _formRepo;

    public SetQuickPeekFormCommandHandler(IFormRepository formRepo)
    {
        _formRepo = formRepo;
    }

    public async Task HandleAsync(SetQuickPeekFormCommand request, CancellationToken ct)
    {
        await _formRepo.SetQuickPeekFormAsync(request.TableId, request.FormId, ct);
    }
}
