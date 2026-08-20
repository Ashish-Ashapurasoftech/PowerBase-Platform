using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NJsonSchema;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("api/v1/pipelines/webhooks")]
public class WebhookController : ControllerBase
{
    private readonly IAdminRepository _adminRepo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IPipelineExecutionQueue _pipelineExecutionQueue;
    private readonly IQueryContext _queryContext;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(
        IAdminRepository adminRepo,
        IPipelineRepository pipelineRepo,
        IPipelineExecutionQueue pipelineExecutionQueue,
        IQueryContext queryContext,
        ILogger<WebhookController> logger)
    {
        _adminRepo = adminRepo;
        _pipelineRepo = pipelineRepo;
        _pipelineExecutionQueue = pipelineExecutionQueue;
        _queryContext = queryContext;
        _logger = logger;
    }

    [HttpPost("{tenantPublicId}/{stepPublicId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecuteWebhook(
        Guid tenantPublicId,
        Guid stepPublicId,
        CancellationToken ct)
    {
        // 1. Resolve tenant
        var tenantId = await _adminRepo.GetTenantIdByPublicIdAsync(tenantPublicId, ct);
        if (tenantId == null || tenantId <= 0)
        {
            return NotFound(new { error = new { code = "TENANT_NOT_FOUND", message = "Tenant not found." } });
        }

        // 2. Set query context
        _queryContext.SetTenantId(tenantId.Value);

        // 3. Resolve the pipeline step
        var step = await _pipelineRepo.GetStepByPublicIdAsync(stepPublicId, ct);
        if (step == null)
        {
            return NotFound(new { error = new { code = "STEP_NOT_FOUND", message = "Step not found." } });
        }

        // 4. Ensure step is active/not deleted and is a webhook trigger
        if (step.IsDeleted || step.Subtype != "webhook")
        {
            return BadRequest(new { error = new { code = "INVALID_STEP_TYPE", message = "Selected step is not a valid webhook trigger." } });
        }

        // 5. Read webhook configuration from ConfigJson
        var config = string.IsNullOrEmpty(step.ConfigJson)
            ? new WebhookStepConfig()
            : JsonSerializer.Deserialize<WebhookStepConfig>(step.ConfigJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new WebhookStepConfig();

        // 6. Validate Authorization header
        if (config.AuthType == "bearer")
        {
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader))
            {
                return Unauthorized(new { error = new { code = "MISSING_TOKEN", message = "Authorization header is missing." } });
            }

            var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authHeader.Substring(7).Trim()
                : authHeader.Trim();

            if (token != config.AuthSecret)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = new { code = "INVALID_TOKEN", message = "Invalid authorization token." } });
            }
        }

        // 9. Validate request body against JSON Schema
        using var reader = new StreamReader(Request.Body);
        var bodyStr = await reader.ReadToEndAsync(ct);

        if (!string.IsNullOrEmpty(config.JsonSchema))
        {
            try
            {
                var schema = await JsonSchema.FromJsonAsync(config.JsonSchema, ct);
                var errors = schema.Validate(bodyStr);
                if (errors.Count > 0)
                {
                    var validationErrors = errors.Select(e => e.ToString()).ToList();
                    return BadRequest(new { error = new { code = "VALIDATION_FAILED", message = "Payload validation failed.", errors = validationErrors } });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing JSON Schema for webhook step {StepId}", step.Id);
                return BadRequest(new { error = new { code = "INVALID_SCHEMA_CONFIG", message = "The configured JSON schema is invalid." } });
            }
        }

        // 11. Extract CorrelationId and Depth from headers
        var correlationId = Request.Headers["X-PowerBase-Correlation-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = Guid.NewGuid().ToString();
        }

        var depthStr = Request.Headers["X-PowerBase-Depth"].FirstOrDefault();
        var depth = 1;
        if (!string.IsNullOrEmpty(depthStr) && int.TryParse(depthStr, out var parsedDepth))
        {
            depth = parsedDepth;
        }

        // 14. If depth > 10, reject immediately
        if (depth > 10)
        {
            return BadRequest(new { error = new { code = "RECURSION_LIMIT_EXCEEDED", message = "Loop recursion limit exceeded." } });
        }

        // 15. Create PipelineExecutionTask with deterministic MessageId
        var providerEventId = Request.Headers["X-PowerBase-Webhook-Id"].FirstOrDefault() 
                              ?? Request.Headers["X-GitHub-Delivery"].FirstOrDefault() 
                              ?? string.Empty;

        var hashInput = tenantPublicId.ToString() + "_" + stepPublicId.ToString() + "_" + (bodyStr ?? string.Empty) + "_" + providerEventId;
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
        var guidBytes = new byte[16];
        Array.Copy(hashBytes, guidBytes, 16);
        var messageId = new Guid(guidBytes);

        var task = new PipelineExecutionTask
        {
            TenantId = tenantId.Value,
            PipelineId = step.PipelineId,
            TriggerEvent = "webhook",
            TriggerPayloadJson = string.IsNullOrEmpty(bodyStr) ? "{}" : bodyStr,
            TriggeredBy = 0, // Public anonymous trigger
            VariablesJson = null,
            CorrelationId = correlationId,
            Depth = depth,
            MessageId = messageId.ToString()
        };

        try
        {
            // 16. Enqueue task
            _pipelineExecutionQueue.QueueTask(task);
        }
        catch (PowerBase.Infrastructure.Pipelines.MessageDeduplicatedException)
        {
            return Ok(new { message = "Deduplicated webhook execution accepted.", correlationId, messageId = messageId.ToString() });
        }
        catch (PowerBase.Infrastructure.Pipelines.MessageCollisionException)
        {
            return Conflict(new { error = new { code = "PAYLOAD_COLLISION", message = "A webhook payload collision was detected for this message." } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue webhook task.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = new { code = "SERVICE_UNAVAILABLE", message = "Failed to enqueue webhook execution." } });
        }

        // 17. Return accepted response
        return Accepted(new { message = "Pipeline execution enqueued successfully.", correlationId, messageId = messageId.ToString() });
    }

    private class WebhookStepConfig
    {
        public string? AuthType { get; set; }
        public string? AuthSecret { get; set; }
        public string? JsonSchema { get; set; }
    }
}
