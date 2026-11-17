// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Registry = Aetheus.Shared.Components.PackageRegistry.PackageRegistryDefaults;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// Recette R-8: the license of the target version of an npm update comes from the npm registry's manifest of that exact version
/// (<c>registry.npmjs.org/&lt;name&gt;/&lt;version&gt;</c>), never from an SBOM. One read through the SSRF-guarded "package-feeds" client (no
/// redirect, bounded body, registry timeout). The <c>license</c> field is read as npm has written it over time, each shape explicitly:
/// a string (the SPDX expression of today's manifests); a legacy object <c>{ "type": "MIT", "url": ... }</c>, whose <c>type</c> is the
/// license; a legacy array, in <c>license</c> or in <c>licenses</c>, of such objects or strings, read as all of them applying (an
/// <c>AND</c>: the array never said it was a choice, so the stricter reading is kept). Anything that is not an SPDX expression
/// (<c>SEE LICENSE IN ...</c>, a sentence, a number) is a failure that says so, and so is a manifest that names no license.
/// </summary>
internal sealed partial class NpmRegistryLicenseReader(IHttpClientFactory clients, ILogger<NpmRegistryLicenseReader> logger) : IRegistryLicenseReader
{
    public const string HttpClientName = "package-feeds";

    public string Ecosystem => "npm";

    public async Task<RegistryLicenseAnswer> ReadAsync(string packageId, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version) || !Name().IsMatch(packageId) || !Version().IsMatch(version))
            return RegistryLicenseAnswer.Failed($"'{packageId} {version}' is not an npm package and version the registry can be asked about.");
        try
        {
            return await ReadFromRegistryAsync(packageId, version, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
                                              && !ct.IsCancellationRequested)
        {
            // Mandatory catch: a registry that does not answer (network, timeout, a refused address, an unreadable body) is an honest
            // failure with its reason, so the update is recorded as refused and evaluated again at the next pass, never a license from nowhere.
            logger.LogWarning(exception, "License of npm package {Package} {Version} could not be read", packageId, version);
            return RegistryLicenseAnswer.Failed($"The npm registry did not answer for {packageId} {version} ({exception.GetType().Name}).");
        }
    }

    private async Task<RegistryLicenseAnswer> ReadFromRegistryAsync(string packageId, string version, CancellationToken ct)
    {
        using var response = await clients.CreateClient(HttpClientName).GetAsync(Address(packageId, version), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return RegistryLicenseAnswer.Failed($"The npm registry has no version {version} of {packageId}.");
        if (!response.IsSuccessStatusCode)
            return RegistryLicenseAnswer.Failed($"The npm registry answered {(int)response.StatusCode} for {packageId} {version}.");
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return RegistryLicenseAnswer.Failed($"The npm registry gave no manifest for {packageId} {version}.");
        if (root.TryGetProperty("license", out var license) && license.ValueKind != JsonValueKind.Null)
            return Read(license, packageId, version);
        if (root.TryGetProperty("licenses", out var licenses) && licenses.ValueKind != JsonValueKind.Null)
            return Read(licenses, packageId, version);
        return NoLicense(packageId, version);
    }

    /// <summary>A scoped name keeps its <c>@scope/</c> path, each part escaped; the version is a path segment of its own.</summary>
    private static Uri Address(string packageId, string version)
    {
        var parts = packageId.Split('/');
        var path = string.Join('/', parts.Select(part => part.StartsWith('@') ? "@" + Uri.EscapeDataString(part[1..]) : Uri.EscapeDataString(part)));
        return new Uri($"{Registry.Npm}/{path}/{Uri.EscapeDataString(version)}");
    }

    private static RegistryLicenseAnswer Read(JsonElement field, string packageId, string version)
    {
        switch (field.ValueKind)
        {
            case JsonValueKind.String:
                return Expression(field.GetString(), packageId, version);
            case JsonValueKind.Object:
                return Expression(LegacyType(field), packageId, version);
            case JsonValueKind.Array:
                var entries = field.EnumerateArray().ToList();
                if (entries.Count == 0) return NoLicense(packageId, version);
                var expressions = new List<string>(entries.Count);
                foreach (var entry in entries)
                {
                    var text = entry.ValueKind switch
                    {
                        JsonValueKind.String => entry.GetString(),
                        JsonValueKind.Object => LegacyType(entry),
                        _ => null
                    };
                    var one = Expression(text, packageId, version);
                    if (one.Expression is null) return one;
                    expressions.Add(one.Expression);
                }

                return RegistryLicenseAnswer.Found(expressions.Count == 1 ? expressions[0] : string.Join(" AND ", expressions.Select(e => $"({e})")));
            default:
                return RegistryLicenseAnswer.Failed($"The npm registry declares a license for {packageId} {version} in a shape that is not read ({field.ValueKind}).");
        }
    }

    private static string? LegacyType(JsonElement entry) =>
        entry.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    private static RegistryLicenseAnswer Expression(string? text, string packageId, string version)
    {
        var expression = text?.Trim();
        if (string.IsNullOrEmpty(expression)) return NoLicense(packageId, version);
        return DependencyUpdateLicense.IsExpression(expression)
            ? RegistryLicenseAnswer.Found(expression)
            : RegistryLicenseAnswer.Failed($"The npm registry declares '{Shortened(expression)}' for {packageId} {version}, which is not an SPDX license expression.");
    }

    private static RegistryLicenseAnswer NoLicense(string packageId, string version) =>
        RegistryLicenseAnswer.Failed($"The npm registry declares no license expression for {packageId} {version}.");

    private static string Shortened(string text) => text.Length <= 80 ? text : text[..80] + "...";

    // npm names: an optional @scope/, then the name, never starting with a dot (no "..", no path of its own); old packages may carry
    // capitals, so the case is not forced.
    [GeneratedRegex(@"^(@[A-Za-z0-9][A-Za-z0-9._~\-]{0,100}/)?[A-Za-z0-9][A-Za-z0-9._~\-]{0,213}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?(\+[0-9A-Za-z.\-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Version();
}
