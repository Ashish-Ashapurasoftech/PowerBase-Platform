using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Net.Sockets;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public static class EmailAttachmentDownloader
{
    public const long MaxBytes = 25 * 1024 * 1024;

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        // Connect to the validated IP itself, preventing DNS rebinding between check and use.
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
                throw new InvalidOperationException("Attachment URLs must resolve to public internet addresses.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }) { Timeout = TimeSpan.FromSeconds(60) };

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Global unicast only; exclude transition and documentation ranges.
            return (bytes[0] & 0xe0) == 0x20 &&
                !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
                !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0) &&
                !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        }
        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
            !(bytes[0] == 169 && bytes[1] == 254) &&
            !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168) &&
            !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
            !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
            !(bytes[0] == 192 && bytes[1] == 0) &&
            !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
            !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    private static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback ||
            (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !IsPublicAddress(ip)))
            throw new InvalidOperationException("Attachment URL must be a public HTTP or HTTPS URL without embedded credentials.");
        return uri;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri url, CancellationToken ct)
    {
        for (var redirects = 0; ; redirects++)
        {
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            try
            {
                if (redirects >= 5 || response.Headers.Location is not { } location)
                    throw new InvalidOperationException("Attachment download has too many redirects or a missing redirect target.");
                var next = ValidateUrl(new Uri(url, location).AbsoluteUri);
                if (url.Scheme == "https" && next.Scheme != "https")
                    throw new InvalidOperationException("Attachment redirects cannot downgrade HTTPS to HTTP.");
                url = next;
            }
            finally { response.Dispose(); }
        }
    }

    public static async Task AddAsync(MailMessage message, IReadOnlyList<PipelineEmailAttachment> attachments, HttpClient client, CancellationToken ct)
    {
        if (attachments.Count > 50) throw new InvalidOperationException("An email may contain at most 50 URL attachments.");
        long total = message.Attachments.Cast<Attachment>().Sum(item => item.ContentStream.Length);
        if (total > MaxBytes) throw new InvalidOperationException("Email attachments exceed the 25 MB total size limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        foreach (var item in attachments)
        {
            var url = ValidateUrl(item.Url);
            using var response = await GetAsync(client, url, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Attachment download failed (HTTP {(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength is long length && length > MaxBytes - total)
                throw new InvalidOperationException("Email attachments exceed the 25 MB total size limit.");
            var name = item.FileName;
            if (string.IsNullOrWhiteSpace(name)) name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
            if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(url.AbsolutePath.Split('/').LastOrDefault() ?? "");
            name = name?.Trim('"').Replace('\\', '/').Split('/').LastOrDefault();
            if (string.IsNullOrWhiteSpace(name)) name = "attachment";
            if (name.Any(char.IsControl)) throw new InvalidOperationException("Attachment file name contains invalid characters.");
            var mime = string.IsNullOrWhiteSpace(item.MimeType) ? response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream" : item.MimeType.Trim();
            var contentType = new ContentType(mime);
            var data = new MemoryStream();
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[81920];
                int count;
                while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += count;
                    if (total > MaxBytes) throw new InvalidOperationException("Email attachments exceed the 25 MB total size limit.");
                    await data.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
                data.Position = 0;
                var attachment = new Attachment(data, contentType) { Name = name };
                message.Attachments.Add(attachment); // MailMessage owns the stream after this point.
            }
            catch { data.Dispose(); throw; }
        }
    }
}
