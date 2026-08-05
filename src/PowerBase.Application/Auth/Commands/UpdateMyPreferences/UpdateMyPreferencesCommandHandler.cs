using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Commands.UpdateMyPreferences;

public class UpdateMyPreferencesCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly IQueryContext _queryContext;

    public UpdateMyPreferencesCommandHandler(IUserRepository userRepo, IQueryContext queryContext)
    {
        _userRepo = userRepo;
        _queryContext = queryContext;
    }

    public async Task<User> HandleAsync(UpdateMyPreferencesCommand command, CancellationToken ct = default)
    {
        if (_queryContext.UserId == 0)
            throw new UnauthorizedActionException("update personal preferences");

        var validator = new UpdateMyPreferencesCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var preferencesStr = JsonSerializer.Serialize(command.Preferences);
        await _userRepo.UpdatePreferencesAsync(_queryContext.UserId, preferencesStr, ct);

        return await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
    }
}
