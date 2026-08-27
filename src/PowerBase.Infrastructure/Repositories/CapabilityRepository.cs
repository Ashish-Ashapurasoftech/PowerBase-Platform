using Dapper;
using PowerBase.Application.Capabilities.Dtos;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class CapabilityRepository : ControlRepositoryBase, ICapabilityRepository
{
    private const string GetActiveCapabilitiesSql = """
        SELECT 
            c.Code AS Id,
            c.Name,
            c.Icon,
            c.Description,
            c.DisplayOrder,
            p.Code AS PermissionCode
        FROM meta.Capability c
        LEFT JOIN meta.CapabilityPermission cp ON cp.CapabilityId = c.Id
        LEFT JOIN meta.Permission p ON p.Id = cp.PermissionId
        WHERE c.IsActive = 1
        ORDER BY c.DisplayOrder, c.Id
        """;

    private const string GetRoleByPublicIdSql = """
        SELECT Id FROM meta.AppRole WHERE PublicId = @rolePublicId AND IsDeleted = 0
        """;

    private const string GetRolePermissionsSql = """
        SELECT p.Code
        FROM meta.AppRolePermission arp
        JOIN meta.AppRole r ON r.Id = arp.AppRoleId
        JOIN meta.Permission p ON p.Id = arp.PermissionId
        WHERE r.PublicId = @rolePublicId AND r.IsDeleted = 0
        """;

    private const string DeleteRolePermissionsSql = """
        DELETE FROM meta.AppRolePermission WHERE AppRoleId = @roleId
        """;

    private const string InsertRolePermissionsSql = """
        INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
        SELECT @roleId, Id FROM meta.Permission WHERE Code IN @codes
        """;

    private readonly ITenantConnectionFactory _tenantConnectionFactory;

    public CapabilityRepository(
        IControlConnectionFactory connectionFactory,
        ITenantConnectionFactory tenantConnectionFactory,
        IQueryContext queryContext)
        : base(connectionFactory, queryContext)
    {
        _tenantConnectionFactory = tenantConnectionFactory;
    }

    public async Task<IReadOnlyList<CapabilityDto>> GetAllActiveAsync(CancellationToken ct = default)
    {
        IEnumerable<CapabilityRow> rows;

        if (QueryContext.TenantId > 0)
        {
            await using var tenantConn = await _tenantConnectionFactory.CreateAsync(ct);
            rows = await tenantConn.QueryAsync<CapabilityRow>(
                new CommandDefinition(GetActiveCapabilitiesSql, cancellationToken: ct));
        }
        else
        {
            await using var connection = await OpenNewConnectionAsync(ct);
            rows = await connection.QueryAsync<CapabilityRow>(
                new CommandDefinition(GetActiveCapabilitiesSql, cancellationToken: ct));
        }

        var capabilityMap = new Dictionary<string, CapabilityDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (!capabilityMap.TryGetValue(row.Id, out var dto))
            {
                dto = new CapabilityDto
                {
                    Id = row.Id,
                    Name = row.Name,
                    Icon = row.Icon,
                    Description = row.Description,
                    DisplayOrder = row.DisplayOrder,
                    Permissions = new List<string>()
                };
                capabilityMap[row.Id] = dto;
            }

            if (!string.IsNullOrWhiteSpace(row.PermissionCode))
            {
                ((List<string>)dto.Permissions).Add(row.PermissionCode);
            }
        }

        return capabilityMap.Values.OrderBy(c => c.DisplayOrder).ToList();
    }

    public async Task<IReadOnlyList<RoleCapabilityDto>> GetRoleCapabilitiesAsync(Guid rolePublicId, CancellationToken ct = default)
    {
        var capabilities = await GetAllActiveAsync(ct);

        await using var tenantConn = await _tenantConnectionFactory.CreateAsync(ct);
        var roleId = await tenantConn.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(GetRoleByPublicIdSql, new { rolePublicId }, cancellationToken: ct));

        if (roleId == null)
            throw new NotFoundException("AppRole", rolePublicId);

        var activePermRows = await tenantConn.QueryAsync<string>(
            new CommandDefinition(GetRolePermissionsSql, new { rolePublicId }, cancellationToken: ct));
        var activePermSet = activePermRows.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<RoleCapabilityDto>();
        foreach (var cap in capabilities)
        {
            var totalCapPerms = cap.Permissions.Count;
            var activeCapPerms = cap.Permissions.Count(p => activePermSet.Contains(p));

            string status;
            bool isEnabled;

            if (totalCapPerms > 0 && activeCapPerms == totalCapPerms)
            {
                status = "full";
                isEnabled = true;
            }
            else if (activeCapPerms > 0)
            {
                status = "partial";
                isEnabled = false;
            }
            else
            {
                status = "none";
                isEnabled = false;
            }

            result.Add(new RoleCapabilityDto
            {
                Id = cap.Id,
                Name = cap.Name,
                Icon = cap.Icon,
                Description = cap.Description,
                DisplayOrder = cap.DisplayOrder,
                IsEnabled = isEnabled,
                Status = status,
                Permissions = cap.Permissions
            });
        }

        return result.OrderBy(r => r.DisplayOrder).ToList();
    }

    public async Task SaveRoleCapabilitiesAsync(Guid rolePublicId, IReadOnlyList<string> capabilityCodes, CancellationToken ct = default)
    {
        var allCaps = await GetAllActiveAsync(ct);
        var requestedCapSet = capabilityCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using var tenantConn = await _tenantConnectionFactory.CreateAsync(ct);
        var roleId = await tenantConn.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(GetRoleByPublicIdSql, new { rolePublicId }, cancellationToken: ct));

        if (roleId == null)
            throw new NotFoundException("AppRole", rolePublicId);

        var currentPermRows = await tenantConn.QueryAsync<string>(
            new CommandDefinition(GetRolePermissionsSql, new { rolePublicId }, cancellationToken: ct));
        var currentPermSet = currentPermRows.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allCapabilityPerms = allCaps.SelectMany(c => c.Permissions).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Preserve non-capability permissions (e.g. data/record permissions)
        var newPerms = currentPermSet.Where(p => !allCapabilityPerms.Contains(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Add permissions for selected capabilities
        foreach (var cap in allCaps.Where(c => requestedCapSet.Contains(c.Id)))
        {
            foreach (var perm in cap.Permissions)
            {
                newPerms.Add(perm);
            }
        }

        await tenantConn.ExecuteAsync(new CommandDefinition(DeleteRolePermissionsSql, new { roleId = roleId.Value }, cancellationToken: ct));
        if (newPerms.Count > 0)
        {
            await tenantConn.ExecuteAsync(new CommandDefinition(InsertRolePermissionsSql, new { roleId = roleId.Value, codes = newPerms }, cancellationToken: ct));
        }
    }

    public async Task UpdateRoleCapabilityAsync(Guid rolePublicId, string capabilityCode, bool enabled, CancellationToken ct = default)
    {
        var allCaps = await GetAllActiveAsync(ct);
        var targetCap = allCaps.FirstOrDefault(c => string.Equals(c.Id, capabilityCode, StringComparison.OrdinalIgnoreCase));
        if (targetCap == null)
            throw new NotFoundException("Capability", capabilityCode);

        await using var tenantConn = await _tenantConnectionFactory.CreateAsync(ct);
        var roleId = await tenantConn.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(GetRoleByPublicIdSql, new { rolePublicId }, cancellationToken: ct));

        if (roleId == null)
            throw new NotFoundException("AppRole", rolePublicId);

        var currentPermRows = await tenantConn.QueryAsync<string>(
            new CommandDefinition(GetRolePermissionsSql, new { rolePublicId }, cancellationToken: ct));
        var currentPermSet = currentPermRows.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (enabled)
        {
            foreach (var perm in targetCap.Permissions)
            {
                currentPermSet.Add(perm);
            }
        }
        else
        {
            // When disabling, find other capabilities that are currently ENABLED (fully active) for this role
            var otherEnabledCaps = allCaps
                .Where(c => !string.Equals(c.Id, capabilityCode, StringComparison.OrdinalIgnoreCase))
                .Where(c => c.Permissions.Count > 0 && c.Permissions.All(p => currentPermSet.Contains(p)))
                .ToList();

            var neededByOtherEnabledCaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var otherCap in otherEnabledCaps)
            {
                foreach (var p in otherCap.Permissions)
                {
                    neededByOtherEnabledCaps.Add(p);
                }
            }

            // Only remove permissions from target capability that are NOT needed by any other enabled capability
            foreach (var p in targetCap.Permissions)
            {
                if (!neededByOtherEnabledCaps.Contains(p))
                {
                    currentPermSet.Remove(p);
                }
            }
        }

        await tenantConn.ExecuteAsync(new CommandDefinition(DeleteRolePermissionsSql, new { roleId = roleId.Value }, cancellationToken: ct));
        if (currentPermSet.Count > 0)
        {
            await tenantConn.ExecuteAsync(new CommandDefinition(InsertRolePermissionsSql, new { roleId = roleId.Value, codes = currentPermSet }, cancellationToken: ct));
        }
    }

    private sealed class CapabilityRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int DisplayOrder { get; set; }
        public string? PermissionCode { get; set; }
    }
}
