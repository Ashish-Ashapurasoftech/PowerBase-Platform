using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateApp;

public class UpdateAppCommandHandler
{
    private readonly IAppRepository _appRepo;

    public UpdateAppCommandHandler(IAppRepository appRepo)
    {
        _appRepo = appRepo;
    }

    public async Task HandleAsync(UpdateAppCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });
        if (command.Name.Length > 200)
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name must be 200 characters or fewer."] });

        var app = await _appRepo.GetByPublicIdAsync(command.PublicId, ct);
        app.Name = command.Name;
        app.Description = command.Description;
        app.Icon = command.Icon;
        app.Color = command.Color;

        await _appRepo.UpdateAsync(app, ct);
    }
}
