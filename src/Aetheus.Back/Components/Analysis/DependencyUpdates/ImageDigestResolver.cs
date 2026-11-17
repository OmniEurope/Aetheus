// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>The digest of an image tag as its registry gave it, or why there is none. Never both, never a guess.</summary>
internal sealed record ImageDigestAnswer(string? Digest, string? Failure)
{
    public static ImageDigestAnswer Found(string digest) => new(digest, null);
    public static ImageDigestAnswer Failed(string reason) => new(null, reason);
}

/// <summary>Resolves the digest an image tag points at right now. A line that pins a digest is moved only to a digest read here.</summary>
internal interface IImageDigestResolver
{
    Task<ImageDigestAnswer> ResolveAsync(string image, string tag, CancellationToken ct);
}

/// <summary>
/// Recette R-24: the digest of <c>image:tag</c> from the registry's HTTP API (<c>HEAD /v2/&lt;repository&gt;/manifests/&lt;tag&gt;</c>, the
/// <c>Docker-Content-Digest</c> of the manifest list, so the pin stays multi-platform). No shell and no container engine: an HTTPS
/// request through the SSRF-guarded "package-feeds" client (no redirect, bounded body, 15 s). Only the two registries the watch
/// knows images of are read, <c>mcr.microsoft.com</c> (anonymous) and Docker Hub (an anonymous pull token from <c>auth.docker.io</c>
/// only): an image name comes from a repository file, so no other host is ever contacted. When the registry cannot be reached or
/// does not say, the answer is a failure with its reason.
/// </summary>
internal sealed partial class RegistryImageDigestResolver(IHttpClientFactory clients, ILogger<RegistryImageDigestResolver> logger) : IImageDigestResolver
{
    public const string HttpClientName = "package-feeds";
    private const string Mcr = "mcr.microsoft.com";
    private const string HubRegistry = "registry-1.docker.io";
    private const string HubAuth = "auth.docker.io";
    private const string HubService = "registry.docker.io";

    private static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json"
    ];

    private sealed record Registry(string Host, string Repository, bool HasTokenService);

    public async Task<ImageDigestAnswer> ResolveAsync(string image, string tag, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(image) || string.IsNullOrWhiteSpace(tag) || !Tag().IsMatch(tag))
            return ImageDigestAnswer.Failed($"'{image}:{tag}' is not an image reference a registry can be asked about.");
        if (RegistryOf(image) is not { } registry)
            return ImageDigestAnswer.Failed($"'{image}' is not an image name on a registry the digest is read from (only {Mcr} and Docker Hub are).");
        try
        {
            return await ReadAsync(registry, tag, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
                                              && !ct.IsCancellationRequested)
        {
            // Mandatory catch: a registry that does not answer (network, timeout, a refused address, an unreadable token) is an honest failure with
            // its reason, so the update is recorded as not written and evaluated again at the next pass, never a digest from nowhere.
            logger.LogWarning(exception, "Digest of {Image}:{Tag} could not be resolved", image, tag);
            return ImageDigestAnswer.Failed($"The registry {registry.Host} did not answer for {image}:{tag} ({exception.GetType().Name}).");
        }
    }

    private async Task<ImageDigestAnswer> ReadAsync(Registry registry, string tag, CancellationToken ct)
    {
        var url = new Uri($"https://{registry.Host}/v2/{registry.Repository}/manifests/{Uri.EscapeDataString(tag)}");
        using var first = await HeadAsync(url, null, ct).ConfigureAwait(false);
        if (first.StatusCode != HttpStatusCode.Unauthorized) return Answer(first, registry, tag);
        if (!registry.HasTokenService)
            return ImageDigestAnswer.Failed($"The registry {registry.Host} refused an anonymous read of {registry.Repository}:{tag}.");

        var token = await TokenAsync(first, registry, ct).ConfigureAwait(false);
        if (token is null)
            return ImageDigestAnswer.Failed($"The registry {registry.Host} gave no anonymous pull token for {registry.Repository}.");
        using var second = await HeadAsync(url, token, ct).ConfigureAwait(false);
        return Answer(second, registry, tag);
    }

    private async Task<HttpResponseMessage> HeadAsync(Uri url, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var type in ManifestTypes) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await clients.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>An anonymous pull token, from the token service the registry names in its challenge, when that service is Docker's own.</summary>
    private async Task<string?> TokenAsync(HttpResponseMessage challenge, Registry registry, CancellationToken ct)
    {
        var bearer = challenge.Headers.WwwAuthenticate.FirstOrDefault(header => header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        if (bearer?.Parameter is not { } parameters) return null;
        var fields = ChallengeField().Matches(parameters).ToDictionary(match => match.Groups[1].Value.ToLowerInvariant(), match => match.Groups[2].Value);
        if (!fields.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri)
            || realmUri.Scheme != Uri.UriSchemeHttps || !string.Equals(realmUri.Host, HubAuth, StringComparison.OrdinalIgnoreCase))
            return null;
        var url = new Uri($"https://{HubAuth}/token?service={HubService}&scope={Uri.EscapeDataString($"repository:{registry.Repository}:pull")}");
        using var response = await clients.CreateClient(HttpClientName).GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);
        foreach (var name in new[] { "token", "access_token" })
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }

        return null;
    }

    private static ImageDigestAnswer Answer(HttpResponseMessage response, Registry registry, string tag)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            return ImageDigestAnswer.Failed($"The registry {registry.Host} has no tag {tag} for {registry.Repository}.");
        if (!response.IsSuccessStatusCode)
            return ImageDigestAnswer.Failed($"The registry {registry.Host} answered {(int)response.StatusCode} for {registry.Repository}:{tag}.");
        var digest = response.Headers.TryGetValues("Docker-Content-Digest", out var values) ? values.FirstOrDefault() : null;
        return digest is not null && Sha256().IsMatch(digest)
            ? ImageDigestAnswer.Found(digest)
            : ImageDigestAnswer.Failed($"The registry {registry.Host} gave no sha256 digest for {registry.Repository}:{tag}.");
    }

    /// <summary>The registry and repository an image name addresses, or null for a registry this resolver does not read.</summary>
    private static Registry? RegistryOf(string image)
    {
        var parts = image.Split('/');
        var hasHost = parts.Length > 1 && (parts[0].Contains('.', StringComparison.Ordinal) || parts[0].Contains(':', StringComparison.Ordinal)
                                           || parts[0] == "localhost");
        var host = hasHost ? parts[0].ToLowerInvariant() : null;
        var repository = string.Join('/', hasHost ? parts[1..] : parts);
        if (repository.Length is 0 or > 255 || !Repository().IsMatch(repository)) return null;
        return host switch
        {
            Mcr => new Registry(Mcr, repository, false),
            null or "docker.io" or "index.docker.io" or HubRegistry =>
                new Registry(HubRegistry, repository.Contains('/', StringComparison.Ordinal) ? repository : "library/" + repository, true),
            _ => null
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"^[a-z0-9]+([._\-/][a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Repository();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();

    [GeneratedRegex("([A-Za-z]+)=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ChallengeField();
}
