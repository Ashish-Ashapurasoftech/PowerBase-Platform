using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Commands.UpdateUserProfile;

public class UpdateUserProfileCommandHandler
{
    private readonly IUserRepository _userRepo;
    private readonly IQueryContext _queryContext;

    public UpdateUserProfileCommandHandler(IUserRepository userRepo, IQueryContext queryContext)
    {
        _userRepo = userRepo;
        _queryContext = queryContext;
    }

    public async Task<User> HandleAsync(UpdateUserProfileCommand command, CancellationToken ct = default)
    {
        if (_queryContext.UserId == 0)
            throw new UnauthorizedActionException("update profile");

        if (string.IsNullOrWhiteSpace(command.FirstName))
            throw new ValidationException(new Dictionary<string, string[]> { ["FirstName"] = ["First Name is required."] });

        var firstName = command.FirstName.Trim();
        var lastName = (command.LastName ?? string.Empty).Trim();

        await _userRepo.UpdateProfileAsync(_queryContext.UserId, firstName, lastName, ct);

        return await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
    }
}
