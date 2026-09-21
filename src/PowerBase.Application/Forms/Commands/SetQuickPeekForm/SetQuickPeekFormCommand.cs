using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Forms.Commands.SetQuickPeekForm;

/// <summary>Flags or un-flags a single form as a Quick Peek form. Multiple forms per table may be
/// flagged; toggling one never affects the others.</summary>
public record SetQuickPeekFormCommand(Guid FormId, bool Enabled);

public class SetQuickPeekFormCommandHandler
{
    private readonly IFormRepository _formRepo;

    public SetQuickPeekFormCommandHandler(IFormRepository formRepo)
    {
        _formRepo = formRepo;
    }

    public async Task HandleAsync(SetQuickPeekFormCommand request, CancellationToken ct)
    {
        await _formRepo.SetQuickPeekFormAsync(request.FormId, request.Enabled, ct);
    }
}
