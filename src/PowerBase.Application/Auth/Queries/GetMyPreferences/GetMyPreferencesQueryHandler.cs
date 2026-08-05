using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Auth.Queries.GetMyPreferences;

public class GetMyPreferencesQueryHandler
{
    private readonly IUserRepository _userRepo;
    private readonly IQueryContext _queryContext;

    public GetMyPreferencesQueryHandler(IUserRepository userRepo, IQueryContext queryContext)
    {
        _userRepo = userRepo;
        _queryContext = queryContext;
    }

    public async Task<User> HandleAsync(GetMyPreferencesQuery query, CancellationToken ct = default)
    {
        if (_queryContext.UserId == 0)
            throw new UnauthorizedActionException("get personal preferences");

        return await _userRepo.GetByIdAsync(_queryContext.UserId, ct);
    }
}
