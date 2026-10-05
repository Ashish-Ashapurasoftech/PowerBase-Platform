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
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using PowerBase.Application.Pipelines;
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

    [AcceptVerbs("GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", Route = "{tenantPublicId:guid}/{stepPublicId:guid}")]
    [RequestSizeLimit(1048576)]
    [ProducesResponseType(StatusCodes.Status200OK)]
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
        if (step.IsDeleted || step.Subtype != "webhook" || step.Type != "trigger" || step.ParentStepId != null)
        {
            return BadRequest(new { error = new { code = "INVALID_STEP_TYPE", message = "Selected step is not a valid webhook trigger." } });
        }

        // 5. Read webhook configuration from ConfigJson
        IncomingWebhookConfig config;
        try { config = IncomingWebhookConfig.Read(step.ConfigJson); config.Validate(); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or CryptographicException)
        {
            return BadRequest(new { error = new { code = "INVALID_CONFIG", message = "The Incoming Request configuration is invalid." } });
        }

        // 5b. Remember any new query-string parameter names so the pipeline builder can list
        // them under URL Parameters once a real request has revealed the webhook's actual shape.
        // Edits the raw ConfigJson node directly (never round-trips it through IncomingWebhookConfig)
        // so every other saved property keeps the exact casing the Angular editor wrote it with.
        if (Request.Query.Count > 0)
        {
            try
            {
                var observedKeys = Request.Query.Keys.Where(k => !string.IsNullOrEmpty(k)).ToList();
                var node = System.Text.Json.Nodes.JsonNode.Parse(
                    string.IsNullOrWhiteSpace(step.ConfigJson) ? "{}" : step.ConfigJson)!.AsObject();
                var knownKeys = node["sampleUrlParamKeys"] is System.Text.Json.Nodes.JsonArray existing
                    ? existing.Select(n => n?.GetValue<string>()).Where(k => !string.IsNullOrEmpty(k)).ToList()!
                    : new List<string>();
                var mergedKeys = knownKeys.Concat(observedKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (mergedKeys.Count != knownKeys.Count)
                {
                    node["sampleUrlParamKeys"] = new System.Text.Json.Nodes.JsonArray(mergedKeys.Select(k => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(k)).ToArray());
                    await _pipelineRepo.UpdateStepConfigJsonAsync(step.Id, node.ToJsonString(), step.RowVersion, ct);
                }
            }
            catch { /* best-effort sample capture; must never block webhook execution */ }
        }

        // 6. Match the six methods displayed by Incoming Request. ANY BELOW accepts all six.
        if (!IncomingWebhookConfig.Methods.Contains(Request.Method, StringComparer.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status405MethodNotAllowed);
        var expectedMethod = string.IsNullOrWhiteSpace(config.MethodType) ? "ANY BELOW" : config.MethodType;
        if (!string.Equals(expectedMethod, "ANY BELOW", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(expectedMethod, Request.Method, StringComparison.OrdinalIgnoreCase))
        {
            return NoContent(); // deliberately ignored: this request does not start the pipeline
        }

        // 7. Validate the selected authentication schema. Bearer remains accepted for existing
        // saved triggers; new incoming-request triggers use no-auth or JWT.
        JsonElement? jwtPayload = null;
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

            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(config.AuthSecret!)))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = new { code = "INVALID_TOKEN", message = "Invalid authorization token." } });
            }
        }
        else if (string.Equals(config.AuthType, "jwt", StringComparison.OrdinalIgnoreCase))
        {
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            var token = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? authHeader[7..].Trim() : authHeader?.Trim();
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(config.PublicKey))
                return Unauthorized(new { error = new { code = "MISSING_TOKEN", message = "A JWT bearer token is required." } });
            try
            {
                using var rsa = RSA.Create();
                using var ec = ECDsa.Create();
                SecurityKey key;
                if (config.JwtAlgorithm.StartsWith("RS")) { rsa.ImportFromPem(config.PublicKey); key = new RsaSecurityKey(rsa); }
                else { ec.ImportFromPem(config.PublicKey); key = new ECDsaSecurityKey(ec); }
                var parameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    RequireExpirationTime = false,
                    ClockSkew = TimeSpan.Zero,
                    ValidAlgorithms = new[] { config.JwtAlgorithm }
                };
                new JwtSecurityTokenHandler().ValidateToken(token, parameters, out var validated);
                jwtPayload = JsonSerializer.SerializeToElement(((JwtSecurityToken)validated).Payload);
            }
            catch (Exception ex) when (ex is SecurityTokenException || ex is ArgumentException || ex is CryptographicException)
            {
                return Unauthorized(new { error = new { code = "INVALID_TOKEN", message = "JWT verification failed." } });
            }
        }

        var pipeline = await _pipelineRepo.GetByIdAsync(step.PipelineId, ct);
        if (pipeline == null || pipeline.IsDeleted) return NotFound();
        if (!pipeline.IsActive) return NoContent();

        // 9. Validate request body against JSON Schema
        if (Request.ContentLength > 1048576) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        // Bound streamed/chunked requests too, instead of allocating an unbounded string.
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > 1048576) return StatusCode(StatusCodes.Status413PayloadTooLarge);
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
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
        if (depth < 1 || depth > 10)
        {
            return BadRequest(new { error = new { code = "RECURSION_LIMIT_EXCEEDED", message = "Loop recursion limit exceeded." } });
        }

        // 15. Create PipelineExecutionTask with deterministic MessageId
        var providerEventId = Request.Headers["X-PowerBase-Webhook-Id"].FirstOrDefault() 
                              ?? Request.Headers["X-GitHub-Delivery"].FirstOrDefault() 
                              ?? string.Empty;

        // Only provider event IDs identify retries. Identical independent requests must run again.
        var hashInput = tenantPublicId.ToString() + "_" + stepPublicId.ToString() + "_" + providerEventId;
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
        var guidBytes = new byte[16];
        Array.Copy(hashBytes, guidBytes, 16);
        var messageId = string.IsNullOrWhiteSpace(providerEventId) ? Guid.NewGuid() : new Guid(guidBytes);

        JsonElement? json = null;
        try { using var parsed = JsonDocument.Parse(bodyStr); json = parsed.RootElement.Clone(); }
        catch (JsonException) { /* Webhooks accept text, XML and empty bodies as well as JSON. */ }
        var payload = JsonSerializer.SerializeToElement(new
        {
            TriggerStepId = step.Id, TriggerStepRefId = step.RefId,
            body = bodyStr, json, method = Request.Method,
            content_type = Request.ContentType ?? "",
            headers = Request.Headers.SelectMany(h => h.Value.Select(v => new { name = h.Key, value = v ?? "" })).ToArray(),
            url_params = Request.Query.ToDictionary(q => q.Key, q => q.Value.Count == 1 ? (object?)q.Value[0] : q.Value.ToArray()),
            origin_ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            jwt_payload = jwtPayload
        });
        try { if (!config.Matches(payload, step.RefId)) return NoContent(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Invalid Incoming Request condition for step {StepId}", step.Id);
            return BadRequest(new { error = new { code = "INVALID_CONDITION", message = "The trigger condition could not be evaluated." } });
        }

        var task = new PipelineExecutionTask
        {
            TenantId = tenantId.Value,
            PipelineId = step.PipelineId,
            TriggerEvent = "webhook",
            TriggerPayloadJson = payload.GetRawText(),
            TriggeredBy = pipeline.CreatedBy,
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
        return Ok(new { message = "Pipeline execution enqueued successfully.", correlationId, messageId = messageId.ToString() });
    }

}
