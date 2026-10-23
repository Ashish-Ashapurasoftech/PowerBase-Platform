using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Fields.Commands.CleanupExemptFieldEncryption;

/// <summary>
/// Application command handler for cleaning up exempt field encryption.
/// Delegates database operations to IExemptFieldEncryptionCleanupService.
/// </summary>
public class CleanupExemptFieldEncryptionCommandHandler
{
    private readonly IExemptFieldEncryptionCleanupService _cleanupService;
    private readonly IQueryContext _queryContext;
    private readonly ILogger<CleanupExemptFieldEncryptionCommandHandler> _logger;

    public CleanupExemptFieldEncryptionCommandHandler(
        IExemptFieldEncryptionCleanupService cleanupService,
        IQueryContext queryContext,
        ILogger<CleanupExemptFieldEncryptionCommandHandler> logger)
    {
        _cleanupService = cleanupService;
        _queryContext = queryContext;
        _logger = logger;
    }

    public async Task<List<CleanupExemptFieldEncryptionResult>> HandleAsync(
        CleanupExemptFieldEncryptionCommand request,
        CancellationToken ct = default)
    {
        if (request.AllTenants)
        {
            _logger.LogInformation("Starting exempt field encryption cleanup across ALL active tenants.");
            return await _cleanupService.CleanupAllTenantsAsync(ct);
        }

        var tenantId = request.TenantId ?? _queryContext.TenantId;
        if (tenantId <= 0)
        {
            _logger.LogWarning("No tenant specified for exempt field encryption cleanup.");
            return new List<CleanupExemptFieldEncryptionResult>
            {
                new() { Error = "No tenant specified." }
            };
        }

        var result = await _cleanupService.CleanupTenantAsync(tenantId, ct);
        return new List<CleanupExemptFieldEncryptionResult> { result };
    }
}
