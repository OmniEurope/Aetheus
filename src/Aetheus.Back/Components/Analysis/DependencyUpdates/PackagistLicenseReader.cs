// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Registry = Aetheus.Shared.Components.PackageRegistry.PackageRegistryDefaults;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// Recette R-8: the license of the target version of a Packagist update comes from the registry's p2 metadata
/// (<c>repo.packagist.org/p2/&lt;vendor&gt;/&lt;package&gt;.json</c>), never from an SBOM. One read through the SSRF-guarded "package-feeds"
/// client (no redirect, bounded body, registry timeout). The entry of the exact version is found by its <c>version</c> (a leading
/// <c>v</c> ignored, as Composer does); its <c>license</c> array names the licenses the package offers, so several entries are a choice
/// (<c>OR</c>), one entry is that license. The metadata is "minified" (Composer 2): an entry only carries what changed since the one
/// before it, <c>__unset</c> removing a field, so the entries are expanded in order before the version's license is read. A version the
/// registry does not list, no license, or an entry that is not an SPDX expression is a failure that says so.
/// </summary>
internal sealed partial class PackagistRegistryLicenseReader(IHttpClientFactory clients, ILogger<PackagistRegistryLicenseReader> logger)
    : IRegistryLicenseReader
{
    public const string HttpClientName = "package-feeds";
    private const string Unset = "__unset";

    public string Ecosystem => "Packagist";

    public async Task<RegistryLicenseAnswer> ReadAsync(string packageId, string version, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(packageId) ? string.Empty : packageId.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(version) || !Name().IsMatch(name) || !Version().IsMatch(version))
            return RegistryLicenseAnswer.Failed($"'{packageId} {version}' is not a Packagist package and version the registry can be asked about.");
        try
        {
            return await ReadFromRegistryAsync(name, version, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
                                              && !ct.IsCancellationRequested)
        {
            // Mandatory catch: a registry that does not answer (network, timeout, a refused address, an unreadable body) is an honest
            // failure with its reason, so the update is recorded as refused and evaluated again at the next pass, never a license from nowhere.
            logger.LogWarning(exception, "License of Packagist package {Package} {Version} could not be read", name, version);
            return RegistryLicenseAnswer.Failed($"The Packagist registry did not answer for {name} {version} ({exception.GetType().Name}).");
        }
    }

    private async Task<RegistryLicenseAnswer> ReadFromRegistryAsync(string name, string version, CancellationToken ct)
    {
        var path = string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
        using var response = await clients.CreateClient(HttpClientName).GetAsync(new Uri($"{Registry.Packagist}/p2/{path}.json"), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return RegistryLicenseAnswer.Failed($"The Packagist registry has no package {name}.");
        if (!response.IsSuccessStatusCode)
            return RegistryLicenseAnswer.Failed($"The Packagist registry answered {(int)response.StatusCode} for {name} {version}.");
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!TryGetEntries(root, name, out var entries))
            return RegistryLicenseAnswer.Failed($"The Packagist registry lists no version of {name}.");

        var minified = root.TryGetProperty("minified", out var format) && format.ValueKind == JsonValueKind.String;
        JsonElement? license = null;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            license = ExpandLicense(entry, license, minified);
            if (IsListedVersion(entry, version))
                return Read(license, name, version);
        }

        return RegistryLicenseAnswer.Failed($"The Packagist registry has no version {version} of {name}.");
    }

    private static bool TryGetEntries(JsonElement root, string name, out JsonElement entries)
    {
        entries = default;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object
            && packages.TryGetProperty(name, out entries) && entries.ValueKind == JsonValueKind.Array;
    }

    // Expanded: a minified entry without the field keeps the one before it; an entry of an unminified file stands alone.
    private static JsonElement? ExpandLicense(JsonElement entry, JsonElement? previous, bool minified)
    {
        if (entry.TryGetProperty("license", out var field))
            return field.ValueKind == JsonValueKind.String && field.GetString() == Unset ? null : field;
        return minified ? previous : null;
    }

    private static bool IsListedVersion(JsonElement entry, string version) =>
        entry.TryGetProperty("version", out var listed) && listed.ValueKind == JsonValueKind.String && SameVersion(listed.GetString()!, version);

    private static bool SameVersion(string listed, string wanted) =>
        string.Equals(listed.TrimStart('v', 'V'), wanted.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    private static RegistryLicenseAnswer Read(JsonElement? license, string name, string version)
    {
        // Composer's own metadata is an array; a bare string (an old file) is read as the one entry it is. Anything else names nothing.
        List<string> expressions = license switch
        {
            { ValueKind: JsonValueKind.String } one => [one.GetString()?.Trim() ?? string.Empty],
            { ValueKind: JsonValueKind.Array } many => [.. many.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() ?? string.Empty : string.Empty)],
            _ => []
        };
        if (expressions.All(string.IsNullOrEmpty))
            return RegistryLicenseAnswer.Failed($"The Packagist registry declares no license expression for {name} {version}.");
        var wrong = expressions.FirstOrDefault(text => !DependencyUpdateLicense.IsExpression(text));
        if (wrong is not null)
            return RegistryLicenseAnswer.Failed($"The Packagist registry declares '{Shortened(wrong)}' for {name} {version}, which is not an SPDX license expression.");
        return RegistryLicenseAnswer.Found(expressions.Count == 1 ? expressions[0] : string.Join(" OR ", expressions.Select(e => $"({e})")));
    }

    private static string Shortened(string text) => text.Length <= 80 ? text : text[..80] + "...";

    [GeneratedRegex(@"^[a-z0-9]([_.-]?[a-z0-9]+)*/[a-z0-9](([_.]|-{1,2})?[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Name();

    [GeneratedRegex(@"^[vV]?\d+(\.\d+){0,3}(-[0-9A-Za-z.\-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Version();
}
