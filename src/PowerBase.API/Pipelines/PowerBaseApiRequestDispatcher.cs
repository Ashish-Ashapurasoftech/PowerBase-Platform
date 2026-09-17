using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.API.Middleware;
using System.Net;

namespace PowerBase.API.Pipelines;

/// <summary>Uses normal MVC routing, binding and authorization filters with a verified pipeline identity.</summary>
public sealed class PowerBaseApiRequestDispatcher : IPipelineApiRequestDispatcher
{
    private readonly Lazy<RequestDelegate> _pipeline;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _root;
    public PowerBaseApiRequestDispatcher(IServiceProvider root, IConfiguration configuration)
    {
        _configuration = configuration;
        _root = root;
        _pipeline = new(() => {
            var builder = new ApplicationBuilder(root);
            builder.UseMiddleware<ExceptionHandlingMiddleware>();
            builder.UseRouting();
            builder.UseEndpoints(endpoints => endpoints.MapControllers());
            return builder.Build();
        });
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, IServiceProvider connectionServices, CancellationToken ct)
    {
        var identity = connectionServices.GetRequiredService<IQueryContext>();
        if (identity.UserId <= 0 || identity.TenantId <= 0)
            throw new UnauthorizedAccessException("A verified PowerBase account is required.");
        var uri = request.RequestUri ?? throw new InvalidOperationException("URL is required.");
        // Relative URLs never leave this process. Full URLs must match a configured API origin.
        if (uri.IsAbsoluteUri && uri.Host != "powerbase.internal")
        {
            var origins = new[] { _configuration["PipelineExecution:ApiBaseUrl"], _configuration["urls"] }
                .Where(value => !string.IsNullOrWhiteSpace(value)).SelectMany(value => value!.Split(';'));
            if (!origins.Any(value => Uri.TryCreate(value, UriKind.Absolute, out var allowed) && allowed.Scheme == uri.Scheme && allowed.Authority == uri.Authority))
                throw new InvalidOperationException("Full PowerBase URLs must match PipelineExecution:ApiBaseUrl. Use a relative API path for this account.");
        }
        await using var scope = _root.CreateAsyncScope();
        var scopedIdentity = scope.ServiceProvider.GetRequiredService<IQueryContext>();
        scopedIdentity.SetTenantId(identity.TenantId);
        scopedIdentity.SetUserIdentity(identity.UserId, identity.IsSuperAdmin, identity.UserName, identity.UserEmail, identity.Permissions, identity.TenantRole);
        scopedIdentity.SetTokenScope(identity.IsUserToken, identity.TokenAccessAllApps, identity.AllowedAppIds);
        scopedIdentity.IsPipelineExecution = true;
        scopedIdentity.PipelineDepth = identity.PipelineDepth;
        scopedIdentity.PipelineChainJson = identity.PipelineChainJson;
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = ct };
        context.Request.Method = request.Method.Method;
        context.Request.Scheme = uri.Scheme;
        context.Request.Host = new HostString(uri.Authority);
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PowerBase account authentication cannot be overridden by request headers.");
            context.Request.Headers[header.Key] = header.Value.ToArray();
        }
        if (request.Content != null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            foreach (var header in request.Content.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody(bytes.Length > 0));
        }
        using var output = new BoundedResponseStream();
        context.Response.Body = output;
        await _pipeline.Value(context);
        var response = new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode) { Content = new ByteArrayContent(output.ToArray()) };
        foreach (var header in context.Response.Headers)
            if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        return response;
    }
    private sealed class BoundedResponseStream : MemoryStream
    {
        private void Check(int count) { if (Length + count > MakeRequestExecutor.MaxBytes) throw new InvalidOperationException("PowerBase response content exceeds 10 MB."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Check(count); return base.WriteAsync(buffer, offset, count, ct); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); return base.WriteAsync(buffer, ct); }
    }
    private sealed record HasBody(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;
}
