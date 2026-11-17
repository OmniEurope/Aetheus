// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Aetheus.Back.Components.Shared.Maven;

namespace Aetheus.Back.Components.PackageFeeds;

/// <summary>
/// Reads the whole list of versions a registry publishes for a package, over the same SSRF-guarded
/// "package-feeds" client as <see cref="PackageVersionResolver"/>. An answer that is missing, cut short or not
/// shaped as the registry documents it gives no version: the outcome says why and the list stays empty.
/// </summary>
internal sealed partial class PackageVersionLister(
    IHttpClientFactory httpClientFactory,
    ILogger logger,
    TimeProvider timeProvider)
{
    private static readonly PackageVersionListResult NoVersion = new(PackageResolveOutcome.Error, []);

    public async Task<PackageVersionListResult> ListAsync(
        PackageFeedType feedType, string upstreamUrl, string packageName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return Without(PackageResolveOutcome.NotFound);
        try
        {
            return feedType switch
            {
                PackageFeedType.NuGet => await ListNuGetAsync(upstreamUrl, packageName, ct).ConfigureAwait(false),
                PackageFeedType.Npm => await ListNpmAsync(upstreamUrl, packageName, ct).ConfigureAwait(false),
                PackageFeedType.PyPI => await ListPyPiAsync(upstreamUrl, packageName, ct).ConfigureAwait(false),
                PackageFeedType.Maven => await ListMavenAsync(upstreamUrl, packageName, ct).ConfigureAwait(false),
                PackageFeedType.Packagist => await ListPackagistAsync(upstreamUrl, packageName, ct).ConfigureAwait(false),
                _ => Without(PackageResolveOutcome.Unsupported)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException
                                       or XmlException or FormatException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Package version list failed for {Feed} {Package}", feedType, packageName);
            return NoVersion;
        }
    }

    private static PackageVersionListResult Without(PackageResolveOutcome outcome, TimeSpan? retryAfter = null) =>
        new(outcome, [], retryAfter);

    private static PackageVersionListResult Listed(List<PublishedPackageVersion> versions) =>
        versions.Count == 0 ? Without(PackageResolveOutcome.NotFound) : new(PackageResolveOutcome.Resolved, versions);

    /// <summary>Reads the answer to <paramref name="url"/>: the outcome the status settles, otherwise the body.</summary>
    private async Task<(PackageVersionListResult? Early, string Body)> GetAsync(string url, CancellationToken ct)
    {
        using var response = await httpClientFactory.CreateClient("package-feeds").GetAsync(url, ct).ConfigureAwait(false);
        if (RegistryResponses.EarlyOutcome(response, timeProvider) is { } settled)
            return (Without(settled.Outcome, settled.RetryAfter), string.Empty);
        return (null, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private async Task<PackageVersionListResult> ListNuGetAsync(string upstreamUrl, string name, CancellationToken ct)
    {
        // Flat container: { "versions": [ "1.0.0", ... ] }, prereleases included.
        var origin = RegistryResponses.ResolveOrigin(upstreamUrl) ?? PackageRegistryDefaults.NuGet;
        var (early, body) = await GetAsync(
            $"{origin}/v3-flatcontainer/{Uri.EscapeDataString(name.ToLowerInvariant())}/index.json", ct).ConfigureAwait(false);
        if (early is { } settled) return settled;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
            return NoVersion;
        return Listed([.. versions.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            .Select(v => new PublishedPackageVersion(v.GetString()!, null))]);
    }

    private async Task<PackageVersionListResult> ListNpmAsync(string upstreamUrl, string name, CancellationToken ct)
    {
        // Packument: { "versions": { "1.0.0": { ... } }, "time": { "1.0.0": "2020-01-01T00:00:00.000Z" } }.
        var origin = RegistryResponses.ResolveOrigin(upstreamUrl) ?? PackageRegistryDefaults.Npm;
        var (early, body) = await GetAsync($"{origin}/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        if (early is { } settled) return settled;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object)
            return NoVersion;
        var time = root.TryGetProperty("time", out var times) && times.ValueKind == JsonValueKind.Object ? times : (JsonElement?)null;
        return Listed([.. versions.EnumerateObject().Select(v => new PublishedPackageVersion(v.Name, PublishedAt(time, v.Name)))]);
    }

    private static DateTime? PublishedAt(JsonElement? time, string version) =>
        time is { } times && times.TryGetProperty(version, out var value)
        && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var at)
            ? at.ToUniversalTime()
            : null;

    private async Task<PackageVersionListResult> ListPyPiAsync(string upstreamUrl, string name, CancellationToken ct)
    {
        // { "releases": { "1.0": [ { "yanked": false, "upload_time_iso_8601": "..." } ] } }: a release with no
        // file, or only yanked ones, cannot be installed and is not a version to move to.
        var origin = RegistryResponses.ResolveOrigin(upstreamUrl) ?? PackageRegistryDefaults.PyPi;
        var (early, body) = await GetAsync($"{origin}/pypi/{Uri.EscapeDataString(name)}/json", ct).ConfigureAwait(false);
        if (early is { } settled) return settled;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Object)
            return NoVersion;
        var listed = new List<PublishedPackageVersion>();
        foreach (var release in releases.EnumerateObject())
        {
            if (release.Value.ValueKind != JsonValueKind.Array) return NoVersion;
            var file = release.Value.EnumerateArray().FirstOrDefault(IsInstallable);
            if (file.ValueKind == JsonValueKind.Object) listed.Add(new PublishedPackageVersion(release.Name, UploadedAt(file)));
        }

        return Listed(listed);
    }

    private static bool IsInstallable(JsonElement file) =>
        file.ValueKind == JsonValueKind.Object
        && !(file.TryGetProperty("yanked", out var yanked) && yanked.ValueKind == JsonValueKind.True);

    private static DateTime? UploadedAt(JsonElement file) =>
        file.TryGetProperty("upload_time_iso_8601", out var up) && up.ValueKind == JsonValueKind.String && up.TryGetDateTime(out var at)
            ? at.ToUniversalTime()
            : null;

    private async Task<PackageVersionListResult> ListMavenAsync(string upstreamUrl, string name, CancellationToken ct)
    {
        // name is groupId:artifactId (a trailing :spec, accepted by ResolveLatestAsync, is not a version list's business).
        var parts = name.Trim().Split(':', 3);
        if (parts.Length < 2 || !IsMavenCoordinate(parts[0]) || !IsMavenCoordinate(parts[1]))
            return Without(PackageResolveOutcome.NotFound);
        var root = RegistryResponses.ResolveOrigin(upstreamUrl) is null
            ? PackageRegistryDefaults.Maven
            : new Uri(upstreamUrl.Trim()).GetLeftPart(UriPartial.Path).TrimEnd('/');
        var (early, body) = await GetAsync(
            $"{root}/{parts[0].Replace('.', '/')}/{parts[1]}/{MavenMetadata.FileName}", ct).ConfigureAwait(false);
        if (early is { } settled) return settled;

        return Listed([.. MavenMetadata.ReadArtifact(body).Versions.Select(v => new PublishedPackageVersion(v, null))]);
    }

    private static bool IsMavenCoordinate(string value)
        => value.Length is > 0 and <= 200
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
           && !value.StartsWith('.') && !value.Contains("..", StringComparison.Ordinal);

    private async Task<PackageVersionListResult> ListPackagistAsync(string upstreamUrl, string name, CancellationToken ct)
    {
        // p2 metadata: { "packages": { "vendor/name": [ { "version": "1.0.0", "time": "..." }, ... ] } }, tagged
        // releases only (branches live in the separate ~dev file). The name is checked before it reaches the
        // address: Packagist names are vendor/package in lower case, nothing else may become a path.
        var lower = name.Trim().ToLowerInvariant();
        if (!PackagistName().IsMatch(lower)) return Without(PackageResolveOutcome.NotFound);
        var origin = RegistryResponses.ResolveOrigin(upstreamUrl) ?? PackageRegistryDefaults.Packagist;
        var path = string.Join('/', lower.Split('/').Select(Uri.EscapeDataString));
        var (early, body) = await GetAsync($"{origin}/p2/{path}.json", ct).ConfigureAwait(false);
        if (early is { } settled) return settled;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Object
            || !packages.TryGetProperty(lower, out var entries) || entries.ValueKind != JsonValueKind.Array)
            return NoVersion;
        var listed = new List<PublishedPackageVersion>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (VersionOf(entry) is not { } version) return NoVersion;
            if (!IsBranch(version)) listed.Add(new PublishedPackageVersion(version, PackagistTime(entry)));
        }

        return Listed(listed);
    }

    /// <summary>The version an entry of the p2 metadata states, or null when it states none readable.</summary>
    private static string? VersionOf(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("version", out var version)
        && version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString())
            ? version.GetString()
            : null;

    private static bool IsBranch(string version) =>
        version.StartsWith("dev-", StringComparison.OrdinalIgnoreCase) || version.EndsWith("-dev", StringComparison.OrdinalIgnoreCase);

    private static DateTime? PackagistTime(JsonElement entry) =>
        entry.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.String && time.TryGetDateTime(out var at)
            ? at.ToUniversalTime()
            : null;

    [GeneratedRegex(@"^[a-z0-9]([_.-]?[a-z0-9]+)*/[a-z0-9](([_.]|-{1,2})?[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex PackagistName();
}
