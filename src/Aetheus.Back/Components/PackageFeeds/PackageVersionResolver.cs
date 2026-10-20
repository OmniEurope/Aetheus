// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Xml;
using Aetheus.Back.Components.Shared.Maven;

namespace Aetheus.Back.Components.PackageFeeds;

/// <summary>
/// Resolves latest package versions against real registries (NuGet, npm, PyPI, Maven) over the SSRF-guarded
/// "package-feeds" HttpClient. Docker / Generic have no resolver yet and return Unsupported.
/// </summary>
public sealed class PackageVersionResolver(
    IHttpClientFactory httpClientFactory,
    ILogger<PackageVersionResolver> logger,
    TimeProvider timeProvider)
    : IPackageVersionResolver
{
    public async Task<PackageResolveResult> ResolveLatestAsync(
        PackageFeedType feedType, string upstreamUrl, string packageName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return new PackageResolveResult(PackageResolveOutcome.NotFound, null, null);

        try
        {
            return feedType switch
            {
                PackageFeedType.NuGet => await ResolveNuGetAsync(upstreamUrl, packageName, ct).ConfigureAwait(false),
                PackageFeedType.Npm => await ResolveNpmAsync(upstreamUrl, packageName, ct).ConfigureAwait(false),
                PackageFeedType.PyPI => await ResolvePyPiAsync(upstreamUrl, packageName, ct).ConfigureAwait(false),
                PackageFeedType.Maven => await ResolveMavenAsync(upstreamUrl, packageName, ct).ConfigureAwait(false),
                _ => new PackageResolveResult(PackageResolveOutcome.Unsupported, null, null)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException
                                       or XmlException or FormatException)
        {
            logger.LogWarning(ex, "Package resolve failed for {Feed} {Package}", feedType, packageName);
            return new PackageResolveResult(PackageResolveOutcome.Error, null, null);
        }
    }

    private HttpClient Client() => httpClientFactory.CreateClient("package-feeds");

    private async Task<PackageResolveResult> ResolveNuGetAsync(string upstreamUrl, string name, CancellationToken ct)
    {
        // Flat container: {base}/v3-flatcontainer/{id-lower}/index.json -> { "versions": [ ... ] } ascending, prereleases included.
        var origin = ResolveOrigin(upstreamUrl) ?? PackageRegistryDefaults.NuGet;
        var id = name.ToLowerInvariant();
        using var response = await Client().GetAsync($"{origin}/v3-flatcontainer/{Uri.EscapeDataString(id)}/index.json", ct).ConfigureAwait(false);
        if (TryGetEarlyResult(response, out var earlyResult)) return earlyResult;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.GetArrayLength() == 0)
            return new PackageResolveResult(PackageResolveOutcome.NotFound, null, null);

        // Prefer the last STABLE version (no SemVer prerelease '-' suffix); fall back to the last
        // overall only if every listed version is a prerelease, so we never surface a pre as "latest stable".
        string? latest = null;
        foreach (var v in versions.EnumerateArray())
        {
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s) && !s.Contains('-', StringComparison.Ordinal)) latest = s;
        }
        latest ??= versions[versions.GetArrayLength() - 1].GetString();
        return new PackageResolveResult(PackageResolveOutcome.Resolved, latest, null);
    }

    private async Task<PackageResolveResult> ResolveNpmAsync(string upstreamUrl, string name, CancellationToken ct)
    {
        // {base}/{name} -> { "dist-tags": { "latest": v }, "time": { v: iso } }.
        var origin = ResolveOrigin(upstreamUrl) ?? PackageRegistryDefaults.Npm;
        using var response = await Client().GetAsync($"{origin}/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        if (TryGetEarlyResult(response, out var earlyResult)) return earlyResult;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (!root.TryGetProperty("dist-tags", out var tags) || !tags.TryGetProperty("latest", out var latestEl))
            return new PackageResolveResult(PackageResolveOutcome.NotFound, null, null);

        var latest = latestEl.GetString();
        DateTime? published = null;
        if (latest is not null && root.TryGetProperty("time", out var time)
            && time.TryGetProperty(latest, out var t) && t.TryGetDateTime(out var dt))
            published = dt.ToUniversalTime();

        return new PackageResolveResult(PackageResolveOutcome.Resolved, latest, published);
    }

    private async Task<PackageResolveResult> ResolvePyPiAsync(string upstreamUrl, string name, CancellationToken ct)
    {
        // {base}/pypi/{name}/json -> { "info": { "version": v }, "releases": { v: [ { "upload_time_iso_8601" } ] } }.
        var origin = ResolveOrigin(upstreamUrl) ?? PackageRegistryDefaults.PyPi;
        using var response = await Client().GetAsync($"{origin}/pypi/{Uri.EscapeDataString(name)}/json", ct).ConfigureAwait(false);
        if (TryGetEarlyResult(response, out var earlyResult)) return earlyResult;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (!root.TryGetProperty("info", out var info) || !info.TryGetProperty("version", out var vEl))
            return new PackageResolveResult(PackageResolveOutcome.NotFound, null, null);

        var latest = vEl.GetString();
        DateTime? published = null;
        if (latest is not null && root.TryGetProperty("releases", out var releases)
            && releases.TryGetProperty(latest, out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0
            && files[0].TryGetProperty("upload_time_iso_8601", out var up) && up.TryGetDateTime(out var dt))
            published = dt.ToUniversalTime();

        return new PackageResolveResult(PackageResolveOutcome.Resolved, latest, published);
    }

    /// <summary>
    /// <paramref name="name"/> is <c>groupId:artifactId</c>, optionally followed by <c>:spec</c> where spec
    /// is <c>RELEASE</c> (the default), <c>LATEST</c>, a version range such as <c>[1.0,2.0)</c> or an exact
    /// version. Reads <c>{repository}/{group path}/{artifactId}/maven-metadata.xml</c>; a selected
    /// <c>-SNAPSHOT</c> is resolved to its newest timestamped build from the snapshot metadata.
    /// </summary>
    private async Task<PackageResolveResult> ResolveMavenAsync(string upstreamUrl, string name, CancellationToken ct)
    {
        var parts = name.Trim().Split(':', 3);
        if (parts.Length < 2 || !IsMavenCoordinate(parts[0]) || !IsMavenCoordinate(parts[1]))
            return new PackageResolveResult(PackageResolveOutcome.NotFound, null, null);
        var specText = parts.Length == 3 ? parts[2].Trim() : MavenVersionSpec.Release;
        var spec = MavenVersionSpec.Parse(specText);

        var artifactUrl = $"{ResolveRepositoryRoot(upstreamUrl)}/{parts[0].Replace('.', '/')}/{parts[1]}";
        using var response = await Client().GetAsync($"{artifactUrl}/{MavenMetadata.FileName}", ct).ConfigureAwait(false);
        if (TryGetEarlyResult(response, out var earlyResult)) return earlyResult;
        var metadata = MavenMetadata.ReadArtifact(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        // Maven answers RELEASE and LATEST from the <release> and <latest> elements; a repository that
        // omits them still lists its versions, from which the same rule picks.
        var selected = specText switch
        {
            MavenVersionSpec.Release => metadata.Release ?? spec.Select(metadata.Versions),
            MavenVersionSpec.Latest => metadata.Latest ?? spec.Select(metadata.Versions),
            _ => spec.Select(metadata.Versions)
        };
        if (selected is null)
            return new PackageResolveResult(PackageResolveOutcome.NotFound, null, null);
        return selected.EndsWith(MavenSnapshot.Suffix, StringComparison.Ordinal)
            ? await ResolveMavenSnapshotAsync(artifactUrl, selected, ct).ConfigureAwait(false)
            : new PackageResolveResult(PackageResolveOutcome.Resolved, selected, null);
    }

    private async Task<PackageResolveResult> ResolveMavenSnapshotAsync(string artifactUrl, string selected, CancellationToken ct)
    {
        using var snapshotResponse = await Client()
            .GetAsync($"{artifactUrl}/{selected}/{MavenMetadata.FileName}", ct).ConfigureAwait(false);
        if (TryGetEarlyResult(snapshotResponse, out var snapshotEarly))
            return snapshotEarly.Outcome == PackageResolveOutcome.NotFound
                ? new PackageResolveResult(PackageResolveOutcome.Resolved, selected, null)
                : snapshotEarly;
        var build = MavenMetadata.ReadSnapshotValue(
            await snapshotResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false), selected);
        DateTime? builtAt = build is not null && MavenSnapshot.TryParseTimestamped(build, out var parsed)
            ? DateTime.ParseExact(parsed.Timestamp, "yyyyMMdd.HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
            : null;
        return new PackageResolveResult(PackageResolveOutcome.Resolved, build ?? selected, builtAt);
    }

    private static bool IsMavenCoordinate(string value)
        => value.Length is > 0 and <= 200
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
           && !value.StartsWith('.') && !value.Contains("..", StringComparison.Ordinal);

    /// <summary>A Maven repository lives under a path (<c>/maven2</c>), so unlike the other registries the
    /// whole URL is kept, not only its origin.</summary>
    private static string ResolveRepositoryRoot(string? url)
        => ResolveOrigin(url) is null
            ? PackageRegistryDefaults.Maven
            : new Uri(url!.Trim()).GetLeftPart(UriPartial.Path).TrimEnd('/');

    private bool TryGetEarlyResult(
        HttpResponseMessage response,
        out PackageResolveResult result)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            result = RateLimited(response);
            return true;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            result = new PackageResolveResult(PackageResolveOutcome.NotFound, null, null);
            return true;
        }

        response.EnsureSuccessStatusCode();
        result = default!;
        return false;
    }

    private PackageResolveResult RateLimited(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter?.Delta;
        if (retry is null && response.Headers.RetryAfter?.Date is { } retryAt)
            retry = retryAt - timeProvider.GetUtcNow();
        if (retry <= TimeSpan.Zero)
            retry = null;
        return new PackageResolveResult(PackageResolveOutcome.RateLimited, null, null, retry);
    }

    private static string? ResolveOrigin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return null;
        return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }
}
