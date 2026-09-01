using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Tables.Commands.CreateTable;

public class CreateTableResult
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;
    public string? SingularLabel { get; init; }
    public string? PluralLabel { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string PhysicalTableName { get; init; } = string.Empty;
    public int RecordCount { get; init; }
    public bool IsShowInBar { get; init; }
    public DateTime CreatedOn { get; init; }
}

public class CreateTableCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly IAppSeeder _appSeeder;

    public CreateTableCommandHandler(
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IAppSeeder appSeeder)
    {
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _appSeeder = appSeeder;
    }

    public async Task<CreateTableResult> HandleAsync(CreateTableCommand command, CancellationToken ct = default)
    {
        var validator = new CreateTableCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);

        if (await _tableRepo.NameExistsInAppAsync(app.Id, command.Name, ct))
            throw new DuplicateException("Table", "name", command.Name);

        var alias = await GenerateUniqueAliasAsync(app.Id, command.Name, ct);

        var table = new AppTable
        {
            AppId = app.Id,
            Name = command.Name,
            Alias = alias,
            SingularLabel = command.SingularLabel,
            PluralLabel = command.PluralLabel,
            Description = command.Description,
            Icon = command.Icon,
            CreatedBy = _queryContext.UserId,
        };

        table = await _appSeeder.CreateTableWithDefaultsAsync(table, _queryContext.UserId, ct: ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.AppTable, table.PublicId.ToString(), $"Table added: {table.Name}", appId: app.Id, ct: ct);

        return new CreateTableResult
        {
            PublicId = table.PublicId,
            Name = table.Name,
            Alias = table.Alias,
            SingularLabel = table.SingularLabel,
            PluralLabel = table.PluralLabel,
            Description = table.Description,
            Icon = table.Icon,
            PhysicalTableName = table.PhysicalTableName ?? string.Empty,
            RecordCount = 0,
            IsShowInBar = table.IsShowInBar,
            CreatedOn = DateTime.UtcNow,
        };
    }

    /// <summary>Generates this table's stable formula alias from its name (see
    /// <see cref="TableAliasNaming.Generate"/>), appending _2, _3, ... on collision with another
    /// table already in the app. The alias is immutable after creation — a later rename never
    /// regenerates it, so existing Custom Data Rules referencing it keep working.</summary>
    private async Task<string> GenerateUniqueAliasAsync(long appId, string name, CancellationToken ct)
    {
        var baseAlias = TableAliasNaming.Generate(name);
        var alias = baseAlias;
        var suffix = 2;
        while (await _tableRepo.AliasExistsInAppAsync(appId, alias, ct))
        {
            alias = $"{baseAlias}_{suffix}";
            suffix++;
        }
        return alias;
    }
}
