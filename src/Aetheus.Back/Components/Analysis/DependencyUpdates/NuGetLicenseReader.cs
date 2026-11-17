// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Registry = Aetheus.Shared.Components.PackageRegistry.PackageRegistryDefaults;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// Recette R-31: the license of the target version of a NuGet update comes from the registry's own metadata, never from an SBOM (which
/// describes what a project already ships, not the version it moves to). Two reads of nuget.org through the SSRF-guarded "package-feeds"
/// client (no redirect, bounded body): the registration leaf of the version (<c>registration5-gz-semver2/{id}/{version}.json</c>, served
/// gzip-compressed whatever the request says, so the body is decompressed here, with a bound), then the catalog entry that leaf names,
/// whose <c>licenseExpression</c> is the SPDX expression the package declares. A package that declares no expression (a license URL or
/// a license file only) has no license that can be judged: the answer is a failure that says so, not a license read from elsewhere.
/// Only a package an advisory names is ever asked for (the candidates come from the watch's matches), so a private package of a
/// project is not looked up by name by design of the caller, and the catalog address the registration gives is followed only on the
/// registry's own host.
/// </summary>
internal sealed partial class NuGetRegistryLicenseReader(IHttpClientFactory clients, ILogger<NuGetRegistryLicenseReader> logger) : IRegistryLicenseReader
{
    public const string HttpClientName = "package-feeds";
    private static readonly Uri Origin = new(Registry.NuGet);
    private static readonly string Registration = Registry.NuGet + "/v3/registration5-gz-semver2/";
    private const string CatalogPath = "/v3/catalog0/data/";
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    public string Ecosystem => "NuGet";

    public async Task<RegistryLicenseAnswer> ReadAsync(string packageId, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version) || !Id().IsMatch(packageId) || !Version().IsMatch(version))
            return RegistryLicenseAnswer.Failed($"'{packageId} {version}' is not a NuGet package and version the registry can be asked about.");
        var id = packageId.ToLowerInvariant();
        var normalized = NormalizeVersion(version);
        try
        {
            return await ReadFromRegistryAsync(packageId, id, normalized, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException
                                              && !ct.IsCancellationRequested)
        {
            // Mandatory catch: a registry that does not answer (network, timeout, a refused address, an unreadable body) is an honest
            // failure with its reason, so the update is recorded as refused and evaluated again at the next pass, never a license from nowhere.
            logger.LogWarning(exception, "License of NuGet package {Package} {Version} could not be read", packageId, version);
            return RegistryLicenseAnswer.Failed($"The NuGet registry did not answer for {packageId} {version} ({exception.GetType().Name}).");
        }
    }

    private async Task<RegistryLicenseAnswer> ReadFromRegistryAsync(string packageId, string id, string version, CancellationToken ct)
    {
        var leafUrl = new Uri($"{Registration}{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}.json");
        var leaf = await GetAsync(leafUrl, ct).ConfigureAwait(false);
        if (leaf.Status == HttpStatusCode.NotFound)
            return RegistryLicenseAnswer.Failed($"The NuGet registry has no version {version} of {packageId}.");
        if (leaf.Body is null) return RegistryLicenseAnswer.Failed($"The NuGet registry answered {(int)leaf.Status} for {packageId} {version}.");

        using var leafDocument = JsonDocument.Parse(leaf.Body);
        if (leafDocument.RootElement.ValueKind != JsonValueKind.Object || !leafDocument.RootElement.TryGetProperty("catalogEntry", out var entry)
            || entry.ValueKind != JsonValueKind.String || !CatalogAddress(entry.GetString()!, out var catalogUrl))
            return RegistryLicenseAnswer.Failed($"The NuGet registry gave no catalog entry on {Origin.Host} for {packageId} {version}.");

        var catalog = await GetAsync(catalogUrl, ct).ConfigureAwait(false);
        if (catalog.Body is null) return RegistryLicenseAnswer.Failed($"The NuGet catalog answered {(int)catalog.Status} for {packageId} {version}.");
        using var catalogDocument = JsonDocument.Parse(catalog.Body);
        var root = catalogDocument.RootElement;
        var expression = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("licenseExpression", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
        return string.IsNullOrEmpty(expression)
            ? RegistryLicenseAnswer.Failed($"The NuGet registry declares no license expression for {packageId} {version} (a license URL or file only is not judged).")
            : RegistryLicenseAnswer.Found(expression);
    }

    /// <summary>The catalog address the registration names, only when it is HTTPS on the registry's own host and in its catalog tree.</summary>
    private static bool CatalogAddress(string address, out Uri url) =>
        Uri.TryCreate(address, UriKind.Absolute, out url!) && url.Scheme == Uri.UriSchemeHttps
        && string.Equals(url.Host, Origin.Host, StringComparison.OrdinalIgnoreCase) && url.Port == Origin.Port
        && url.AbsolutePath.StartsWith(CatalogPath, StringComparison.Ordinal) && !url.AbsolutePath.Contains("..", StringComparison.Ordinal);

    private async Task<(HttpStatusCode Status, byte[]? Body)> GetAsync(Uri url, CancellationToken ct)
    {
        using var response = await clients.CreateClient(HttpClientName).GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return (response.StatusCode, await Decompressed(bytes, ct).ConfigureAwait(false));
    }

    /// <summary>The registration hive is stored gzip-compressed and served so, without regard to Accept-Encoding, and the guarded client
    /// does not decompress: a body that starts with the gzip magic is decompressed here, never past <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]> Decompressed(byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length < 2 || bytes[0] != 0x1F || bytes[1] != 0x8B) return bytes;
        await using var source = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (result.Length + read > MaxBodyBytes) throw new InvalidDataException("The decompressed registry answer is too large.");
            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }

    /// <summary>The normalized form the registration hive names a version by (nuget.org answers 404 for any other): no build metadata,
    /// lower case, no leading zeros, at least three numeric parts and a fourth one only when it is not zero (4.0 and 4.0.0.0 are 4.0.0).
    /// The input already matched <see cref="Version"/>, so each numeric part is digits only.</summary>
    private static string NormalizeVersion(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var text = (plus < 0 ? version : version[..plus]).ToLowerInvariant();
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var release = dash < 0 ? string.Empty : text[dash..];
        var parts = (dash < 0 ? text : text[..dash]).Split('.').Select(part => part.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0").ToList();
        while (parts.Count < 3) parts.Add("0");
        if (parts.Count == 4 && parts[3] == "0") parts.RemoveAt(3);
        return string.Join('.', parts) + release;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Id();

    [GeneratedRegex(@"^\d+(\.\d+){0,3}(-[0-9A-Za-z.\-]+)?(\+[0-9A-Za-z.\-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Version();
}
