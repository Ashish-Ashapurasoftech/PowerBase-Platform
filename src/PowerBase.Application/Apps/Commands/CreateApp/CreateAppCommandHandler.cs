using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.CreateApp;

public class CreateAppResult
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? Color { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedOn { get; init; }
}

public class CreateAppCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IQueryContext _queryContext;

    public CreateAppCommandHandler(IAppRepository appRepo, IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _queryContext = queryContext;
    }

    public async Task<CreateAppResult> HandleAsync(CreateAppCommand command, CancellationToken ct = default)
    {
        var validator = new CreateAppCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        if (await _appRepo.NameExistsAsync(command.Name, ct))
            throw new DuplicateException("App", "name", command.Name);

        var now = DateTime.UtcNow;
        var app = new App
        {
            TenantId = _queryContext.TenantId,
            OwnerId = _queryContext.UserId,
            Name = command.Name,
            Description = command.Description,
            Icon = command.Icon,
            Color = command.Color,
            Status = "Active",
            CreatedOn = now,
            CreatedBy = _queryContext.UserId,
        };

        var publicId = await _appRepo.CreateAsync(app, ct);

        return new CreateAppResult
        {
            PublicId = publicId,
            Name = app.Name,
            Description = app.Description,
            Icon = app.Icon,
            Color = app.Color,
            Status = app.Status,
            CreatedOn = now,
        };
    }
}
