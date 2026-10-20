// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>Fetch user-requested images through the SSRF-protected client with private and loopback
/// targets blocked, no cookies, and at most three manually validated redirects. Inline only raster
/// images within count and size limits to satisfy the page CSP.</summary>
public sealed class MailboxRemoteImageFetcher(IHttpClientFactory httpClientFactory) : IMailboxRemoteImageFetcher
{
    public const string HttpClientName = "mailbox-images";
    public const int MaxImages = 30;
    public const long MaxImageBytes = 2L * 1024 * 1024;
    public const long MaxTotalBytes = 10L * 1024 * 1024;
    private const int MaxRedirects = 3;

    private static readonly HashSet<string> s_imageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"
    };

    public async Task<IReadOnlyDictionary<string, string>> FetchAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var client = httpClientFactory.CreateClient(HttpClientName);
        long total = 0;
        foreach (var url in urls.Distinct(StringComparer.Ordinal).Take(MaxImages))
        {
            if (total >= MaxTotalBytes) break;
            var image = await FetchOneAsync(client, url, ct).ConfigureAwait(false);
            if (image is null || total + image.Value.Bytes.Length > MaxTotalBytes) continue;
            total += image.Value.Bytes.Length;
            result[url] = $"data:{image.Value.ContentType};base64,{Convert.ToBase64String(image.Value.Bytes)}";
        }
        return result;
    }

    internal static readonly TimeSpan PerImageDeadline = TimeSpan.FromSeconds(15);

    private static async Task<(string ContentType, byte[] Bytes)?> FetchOneAsync(HttpClient client, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url, UriKind.Absolute, out var uri))
            return null;
        // The client timeout stops at the headers; this deadline also bounds a body that drips in.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(PerImageDeadline);
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode) return null;
                return await ReadImageAsync(response.Content, deadline.Token).ConfigureAwait(false);
            }
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException
                                          && !ct.IsCancellationRequested)
        {
            // Mandatory: one image that cannot be fetched stays withheld; the message is still shown.
            return null;
        }
    }

    private static async Task<(string ContentType, byte[] Bytes)?> ReadImageAsync(HttpContent content, CancellationToken ct)
    {
        var type = content.Headers.ContentType?.MediaType;
        if (type is null || !s_imageTypes.Contains(type)) return null;
        if (content.Headers.ContentLength is > MaxImageBytes) return null;

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return (type.ToLowerInvariant(), buffer.ToArray());
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
