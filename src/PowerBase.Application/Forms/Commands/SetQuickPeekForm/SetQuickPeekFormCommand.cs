using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Forms.Commands.SetQuickPeekForm;

/// <summary>Flags or un-flags a single form as a Quick Peek form. Multiple forms per table may be
/// flagged; toggling one never affects the others.</summary>
public record SetQuickPeekFormCommand(Guid FormId, bool Enabled);

/// <summary>Makes a form the table's default Quick Peek form (flagging it if needed).</summary>
public record SetQuickPeekDefaultCommand(Guid FormId);

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

    public async Task HandleAsync(SetQuickPeekDefaultCommand request, CancellationToken ct)
    {
        await _formRepo.SetQuickPeekDefaultAsync(request.FormId, ct);
    }
}
