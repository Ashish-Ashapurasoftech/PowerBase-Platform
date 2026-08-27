using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Search.Commands.SanitizeEncryptedData;

public class SanitizeEncryptedDataCommandHandler
{
    private readonly IQueryContext _queryContext;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly ILogger<SanitizeEncryptedDataCommandHandler> _logger;

    public SanitizeEncryptedDataCommandHandler(
        IQueryContext queryContext,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        ILogger<SanitizeEncryptedDataCommandHandler> logger)
    {
        _queryContext = queryContext;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _logger = logger;
    }

    public async Task HandleAsync(SanitizeEncryptedDataCommand request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting encrypted data sanitization for Tenant {TenantId}.", request.TenantId);
        _queryContext.SetTenantId(request.TenantId);

        int page = 1;
        int totalApps = 0;
        int totalTables = 0;
        int totalSanitizedRecords = 0;

        while (true)
        {
            var apps = await _appRepo.ListAsync(page, 100, cancellationToken);
            if (apps.Count == 0) break;

            foreach (var app in apps)
            {
                totalApps++;
                var tables = await _tableRepo.ListByAppAsync(app.Id, cancellationToken);
                foreach (var table in tables)
                {
                    totalTables++;
                    try
                    {
                        var fields = await _fieldRepo.ListByTableAsync(table.Id, cancellationToken);
                        
                        _logger.LogInformation("Sanitizing Table {TableName} (ID: {TableId}) in App: {AppName}...", table.Name, table.Id, app.Name);
                        
                        var sanitizedCount = await _recordRepo.SanitizeTableEncryptedDataAsync(table, fields, cancellationToken);
                        if (sanitizedCount > 0)
                        {
                            totalSanitizedRecords += sanitizedCount;
                            _logger.LogInformation("Sanitized {Count} records in Table {TableName}.", sanitizedCount, table.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sanitizing Table {TableName} (ID: {TableId}).", table.Name, table.Id);
                    }
                }
            }
            page++;
        }

        _logger.LogInformation("Completed encrypted data sanitization for Tenant {TenantId}. Apps: {AppsCount}, Tables: {TablesCount}, Sanitized Records: {SanitizedCount}.", 
            request.TenantId, totalApps, totalTables, totalSanitizedRecords);
    }
}
