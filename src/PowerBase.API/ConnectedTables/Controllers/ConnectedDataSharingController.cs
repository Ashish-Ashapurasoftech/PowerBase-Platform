using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.ConnectedTables.Models;
using PowerBase.Application.ConnectedTables.Handlers;

namespace PowerBase.API.ConnectedTables.Controllers;

/// <summary>Data sharing between realms: the policy, access requests and their approval, and the tables reading this realm's data.</summary>
[ApiController]
[Route("connected/data-sharing")]
[RequireAuth]
[ConnectedTablesExceptionFilter]
public sealed class ConnectedDataSharingController(DataSharingHandlers handlers) : ControllerBase
{
    [HttpGet("policy")]
    public async Task<IActionResult> GetPolicy(CancellationToken ct) => Ok(new { data = await handlers.GetPolicyAsync(ct) });

    [HttpPut("policy")]
    public async Task<IActionResult> SetPolicy([FromBody] SharingPolicyRequest r, CancellationToken ct) =>
        Ok(new { data = await handlers.SetPolicyAsync(new SetSharingPolicyCommand(r.OutboundPolicy, r.InboundEnabled), ct) });

    /// <summary>Ask another realm for access to its data.</summary>
    [HttpPost("requests")]
    public async Task<IActionResult> RequestAccess([FromBody] DataShareRequestRequest r, CancellationToken ct) =>
        Ok(new { data = await handlers.RequestAsync(new RequestDataShareCommand(r.Realm, r.Note), ct) });

    /// <summary>Requests and shares where this realm is the provider.</summary>
    [HttpGet("incoming")]
    public async Task<IActionResult> Incoming(CancellationToken ct) => Ok(new { data = await handlers.ListIncomingAsync(ct) });

    /// <summary>Requests and shares where this realm is the consumer.</summary>
    [HttpGet("outgoing")]
    public async Task<IActionResult> Outgoing(CancellationToken ct) => Ok(new { data = await handlers.ListOutgoingAsync(ct) });

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApproveDataShareRequest r, CancellationToken ct) =>
        Ok(new { data = await handlers.ApproveAsync(new ApproveDataShareCommand(id, r.Scope, r.AppIds, r.AllowEncryptedFields, r.AllowCrossRegion, r.ExpiresOn, r.RowVersion), ct) });

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RowVersionRequest r, CancellationToken ct) =>
        Ok(new { data = await handlers.RejectAsync(id, r.RowVersion, ct) });

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, [FromBody] RowVersionRequest r, CancellationToken ct) =>
        Ok(new { data = await handlers.RevokeAsync(id, r.RowVersion, ct) });

    /// <summary>Every connected table that reads this realm's data.</summary>
    [HttpGet("links")]
    public async Task<IActionResult> Links(CancellationToken ct) => Ok(new { data = await handlers.ListLinksAsync(ct) });

    [HttpPost("links/{id:guid}/revoke")]
    public async Task<IActionResult> RevokeLink(Guid id, CancellationToken ct)
    {
        await handlers.RevokeLinkAsync(id, ct);
        return NoContent();
    }
}
