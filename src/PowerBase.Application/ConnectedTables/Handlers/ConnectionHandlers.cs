using FluentValidation;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Access;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Mapping;
using PowerBase.Application.Reports;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Exceptions;
using ValidationException = PowerBase.Domain.Exceptions.ValidationException;

namespace PowerBase.Application.ConnectedTables.Handlers;

public sealed record CreateConnectionCommand(string Name, string AuthMode, string? Realm, Guid AppPublicId, string? UserToken, string? AppToken);

public sealed record UpdateConnectionCommand(Guid PublicId, string Name, string? UserToken, string? AppToken, bool ClearAppToken, string RowVersion);

public sealed record PreviewCountCommand(Guid ConnectionPublicId, Guid TablePublicId, FilterGroup? Filter);

public sealed class CreateConnectionCommandValidator : AbstractValidator<CreateConnectionCommand>
{
    public CreateConnectionCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithMessage("Name the connection.").MaximumLength(200);
        RuleFor(c => c.AuthMode).Must(m => ConnectedAuthModes.All.Contains(m)).WithMessage("Choose how the connection signs in.");
        RuleFor(c => c.AppPublicId).NotEmpty().WithMessage("Enter the Application ID of the source app.");
        RuleFor(c => c.UserToken).NotEmpty().When(c => c.AuthMode == ConnectedAuthModes.UserToken).WithMessage("Enter a user token.");
        RuleFor(c => c.Realm).NotEmpty().When(c => c.AuthMode == ConnectedAuthModes.UserToken).WithMessage("Enter the PowerBase URL of the source realm.");
    }
}

public sealed class UpdateConnectionCommandValidator : AbstractValidator<UpdateConnectionCommand>
{
    public UpdateConnectionCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithMessage("Name the connection.").MaximumLength(200);
        RuleFor(c => c.RowVersion).NotEmpty();
    }
}

internal static class ConnectedValidation
{
    public static void Validate<T>(IValidator<T> validator, T value)
    {
        var result = validator.Validate(value);
        if (!result.IsValid)
            throw new ValidationException(result.Errors.GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));
    }
}

/// <summary>Saved connections: create (with every gate checked up front), list, test, change, delete, and browse the source
/// app through them for the wizard.</summary>
public sealed class ConnectionHandlers(
    IConnectedConnectionRepository connections,
    IConnectedControlRepository control,
    ConnectedAccessVerifier verifier,
    IQueryContext context,
    IAuditRepository audit)
{
    public async Task<ConnectionResult> CreateAsync(CreateConnectionCommand command, CancellationToken ct)
    {
        ConnectedValidation.Validate(new CreateConnectionCommandValidator(), command);
        var consumerTenantId = context.TenantId;

        ConnectedCredential credential;
        ConnectedTenantInfo? realm;
        string? tokenHash = null, tokenPrefix = null;
        if (command.AuthMode == ConnectedAuthModes.SameRealmUser)
        {
            realm = await control.GetTenantAsync(consumerTenantId, ct);
            credential = new ConnectedCredential(consumerTenantId, context.UserId, false, true, new HashSet<long>());
        }
        else
        {
            realm = await control.GetTenantBySlugAsync(ConnectedTokens.RealmSlug(command.Realm!), ct);
            if (realm is null || !realm.IsUsable)
                throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "No active PowerBase realm was found at that URL.");
            // The raw token lives only for this call: from here on only its hash and masked prefix are used.
            tokenHash = ConnectedTokens.Hash(command.UserToken!);
            tokenPrefix = ConnectedTokens.Prefix(command.UserToken!);
            credential = await verifier.ResolveUserTokenAsync(tokenHash, realm.Id, ct);
        }
        var appTokenHash = string.IsNullOrWhiteSpace(command.AppToken) ? null : ConnectedTokens.Hash(command.AppToken);

        string appName;
        await using (var access = await verifier.OpenAsync(credential, command.AppPublicId, appTokenHash, consumerTenantId, ct))
            appName = access.App.Name;

        var connection = new ConnectedConnection
        {
            OwnerUserId = context.UserId,
            Name = command.Name.Trim(),
            AuthMode = command.AuthMode,
            RealmSlug = realm?.Slug ?? string.Empty,
            SourceTenantId = credential.SourceTenantId,
            SourceAppPublicId = command.AppPublicId,
            SourceAppName = appName,
            RunAsUserId = credential.RunAsUserId,
            UserTokenHash = tokenHash,
            UserTokenPrefix = tokenPrefix,
            AppTokenHash = appTokenHash,
            Status = ConnectedConnectionStatuses.Active,
        };
        (connection.Id, connection.PublicId) = await connections.CreateAsync(connection, ct);
        await audit.LogActivityAsync("ConnectionCreated", "ConnectedConnection", connection.PublicId.ToString(),
            $"Connection '{connection.Name}' to {appName} ({connection.RealmSlug})", ct: ct);
        return await ToResultAsync((await connections.GetByIdAsync(connection.Id, ct))!, ct);
    }

    public async Task<IReadOnlyList<ConnectionResult>> ListMineAsync(CancellationToken ct)
    {
        var list = await connections.ListByOwnerAsync(context.UserId, ct);
        var names = await control.GetTenantNamesAsync(list.Select(c => c.SourceTenantId).ToList(), ct);
        var results = new List<ConnectionResult>(list.Count);
        foreach (var c in list) results.Add(await ToResultAsync(c, ct, names));
        return results;
    }

    public async Task<ConnectionResult> GetAsync(Guid publicId, CancellationToken ct) => await ToResultAsync(await GetOwnedAsync(publicId, ct), ct);

    /// <summary>Re-runs every gate. A failure is returned as an error (and recorded on the connection).</summary>
    public async Task<ConnectionResult> TestAsync(Guid publicId, CancellationToken ct)
    {
        var connection = await GetOwnedAsync(publicId, ct);
        await using (await verifier.OpenAsync(connection, context.TenantId, ct)) { }
        return await ToResultAsync((await connections.GetByIdAsync(connection.Id, ct))!, ct);
    }

    public async Task<ConnectionResult> UpdateAsync(UpdateConnectionCommand command, CancellationToken ct)
    {
        ConnectedValidation.Validate(new UpdateConnectionCommandValidator(), command);
        var connection = await GetOwnedAsync(command.PublicId, ct);
        connection.RowVersion = ConnectedTokens.RowVersion(command.RowVersion);
        connection.Name = command.Name.Trim();
        if (command.ClearAppToken) connection.AppTokenHash = null;
        if (!string.IsNullOrWhiteSpace(command.AppToken)) connection.AppTokenHash = ConnectedTokens.Hash(command.AppToken);

        var credentialsChanged = command.ClearAppToken || !string.IsNullOrWhiteSpace(command.AppToken) || !string.IsNullOrWhiteSpace(command.UserToken);
        if (!string.IsNullOrWhiteSpace(command.UserToken))
        {
            if (connection.AuthMode != ConnectedAuthModes.UserToken)
                throw new BadRequestException(ConnectedErrorCodes.ConnectionUnusable, "This connection signs in as you; it has no user token to replace.");
            var hash = ConnectedTokens.Hash(command.UserToken);
            var credential = await verifier.ResolveUserTokenAsync(hash, connection.SourceTenantId, ct);
            connection.UserTokenHash = hash;
            connection.UserTokenPrefix = ConnectedTokens.Prefix(command.UserToken);
            connection.RunAsUserId = credential.RunAsUserId;
        }

        if (credentialsChanged)
        {
            // A replaced credential must pass every gate again before it is saved.
            connection.Status = ConnectedConnectionStatuses.Active;
            connection.StatusReason = null;
            await using var access = await verifier.OpenAsync(
                await verifier.ResolveSavedAsync(connection, context.TenantId, ct), connection.SourceAppPublicId, connection.AppTokenHash, context.TenantId, ct);
            connection.SourceAppName = access.App.Name;
            connection.LastVerifiedOn = DateTime.UtcNow;
        }
        await connections.UpdateAsync(connection, ct);
        return await ToResultAsync((await connections.GetByIdAsync(connection.Id, ct))!, ct);
    }

    public async Task DeleteAsync(Guid publicId, string rowVersion, CancellationToken ct)
    {
        var connection = await GetOwnedAsync(publicId, ct);
        var inUse = await connections.CountTablesUsingAsync(connection.Id, ct);
        if (inUse > 0)
            throw ConnectedTableException.Conflict(ConnectedErrorCodes.ConnectionInUse,
                $"{inUse} connected table{(inUse == 1 ? " uses" : "s use")} this connection. Switch {(inUse == 1 ? "it" : "them")} to another connection first.");
        await connections.SoftDeleteAsync(connection.Id, ConnectedTokens.RowVersion(rowVersion), ct);
        await audit.LogActivityAsync("ConnectionDeleted", "ConnectedConnection", connection.PublicId.ToString(), connection.Name, ct: ct);
    }

    /// <summary>Step 2c of the wizard: the source tables the connected user can view.</summary>
    public async Task<IReadOnlyList<SourceTableResult>> ListSourceTablesAsync(Guid connectionPublicId, CancellationToken ct)
    {
        var connection = await GetOwnedAsync(connectionPublicId, ct);
        await using var access = await verifier.OpenAsync(connection, context.TenantId, ct);
        var results = new List<SourceTableResult>();
        foreach (var table in (await access.Scope.ListTablesAsync(access.App.Id, ct)).Where(t => !t.IsDeleted && !t.IsSystem).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            try { await verifier.DescribeAsync(access, table, ct); }
            catch (ConnectedTableException) { continue; } // not viewable by the connected user
            results.Add(new SourceTableResult(table.PublicId, table.Name, table.PluralLabel, table.Description, table.Icon));
        }
        return results;
    }

    /// <summary>Step 3 of the wizard: the source fields, each with what it would become (or why it can't be connected).</summary>
    public async Task<SourceFieldsResult> ListSourceFieldsAsync(Guid connectionPublicId, Guid tablePublicId, CancellationToken ct)
    {
        var connection = await GetOwnedAsync(connectionPublicId, ct);
        await using var access = await verifier.OpenAsync(connection, context.TenantId, ct);
        var source = await verifier.GetSourceTableAsync(access, tablePublicId, ct);
        return new SourceFieldsResult(source.Table.PublicId, source.Table.Name, DescribeFields(source, access), source.Access.ViewFilter is not null || source.Access.RestrictToCreatedBy is not null);
    }

    /// <summary>Step 4 of the wizard: how many source records a draft filter would connect.</summary>
    public async Task<int> PreviewCountAsync(PreviewCountCommand command, CancellationToken ct)
    {
        var connection = await GetOwnedAsync(command.ConnectionPublicId, ct);
        await using var access = await verifier.OpenAsync(connection, context.TenantId, ct);
        var source = await verifier.GetSourceTableAsync(access, command.TablePublicId, ct);
        var filter = ConnectedFilters.Validate(command.Filter, source);
        return await access.Scope.CountAsync(source.Table, source.AllFields, ConnectedFilters.And(filter, source.Access.ViewFilter), source.Access.RestrictToCreatedBy, ct);
    }

    internal static IReadOnlyList<SourceFieldResult> DescribeFields(ConnectedSourceTable source, ConnectedSourceAccess access) =>
        source.VisibleFields
            .OrderBy(f => f.IsSystem ? 0 : 1).ThenBy(f => f.DisplayOrder).ThenBy(f => f.Fid)
            .Select(f =>
            {
                var plan = ConnectedTypeMapper.Plan(f, access.IsCrossTenant);
                var (connectable, reason) = f.IsEncrypted && !access.AllowsEncryptedFields
                    ? (false, "Encrypted fields can't be connected from this realm (the sharing approval doesn't allow it).")
                    : (plan.Connectable, plan.Reason);
                return new SourceFieldResult(f.Fid!.Value, f.Label ?? f.Name, f.TypeCode, f.IsSystem, f.IsEncrypted, connectable, reason,
                    connectable ? plan.DestinationTypeCode : null, connectable ? plan.Conversion : null);
            })
            .ToList();

    internal async Task<ConnectedConnection> GetOwnedAsync(Guid publicId, CancellationToken ct)
    {
        var connection = await connections.GetByPublicIdAsync(publicId, ct);
        if (connection is null || (connection.OwnerUserId != context.UserId && !context.IsSuperAdmin))
            throw new NotFoundException("Connection", publicId);
        return connection;
    }

    internal async Task<ConnectionResult> ToResultAsync(ConnectedConnection c, CancellationToken ct, IReadOnlyDictionary<long, string>? realmNames = null)
    {
        realmNames ??= await control.GetTenantNamesAsync([c.SourceTenantId], ct);
        return new ConnectionResult(c.PublicId, c.Name, c.AuthMode, c.RealmSlug, realmNames.GetValueOrDefault(c.SourceTenantId),
            c.SourceAppPublicId, c.SourceAppName, c.SourceTenantId != context.TenantId, c.UserTokenPrefix, c.AppTokenHash is not null,
            c.Status, c.StatusReason, ConnectedTime.Utc(c.LastVerifiedOn), ConnectedTime.Utc(c.LastUsedOn), await connections.CountTablesUsingAsync(c.Id, ct), ConnectedTime.Utc(c.CreatedOn),
            ConnectedTokens.RowVersion(c.RowVersion));
    }
}

/// <summary>Source filters: only fields the connected user can see may be used.</summary>
public static class ConnectedFilters
{
    public static FilterGroup? Validate(FilterGroup? filter, ConnectedSourceTable source)
    {
        if (filter is null || filter.Nodes.Count == 0) return null;
        var visible = source.VisibleFields.Select(f => (long)f.Fid!.Value).ToHashSet();
        foreach (var fid in Fids(filter))
            if (!visible.Contains(fid))
                throw new ValidationException(new Dictionary<string, string[]> { ["Filter"] = ["The filter uses a field that is not available in the source table."] });
        if (filter.Logic is not ("and" or "or"))
            throw new ValidationException(new Dictionary<string, string[]> { ["Filter"] = ["Choose whether all or any of the conditions must be true."] });
        return filter;
    }

    public static FilterGroup? And(FilterGroup? a, FilterGroup? b)
    {
        if (a is null || a.Nodes.Count == 0) return b is { Nodes.Count: > 0 } ? b : null;
        if (b is null || b.Nodes.Count == 0) return a;
        return new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = a }, new FilterNode { Group = b }] };
    }

    public static IEnumerable<long> Fids(FilterGroup group)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                yield return c.FieldId;
                if (c.ValueFieldId is { } other) yield return other;
            }
            if (node.Group is { } g) foreach (var fid in Fids(g)) yield return fid;
        }
    }
}
