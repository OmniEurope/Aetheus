// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>A CVE that Microsoft fixed in a release newer than the installed runtime.</summary>
/// <param name="CveId">The CVE identifier, as Microsoft lists it.</param>
/// <param name="CveUrl">The reference URL Microsoft gives for it, when it gives one.</param>
/// <param name="FixedIn">The first release that lists the CVE.</param>
/// <param name="FixedOn">The date of that release, as Microsoft writes it.</param>
/// <param name="LatestSecurity">The newest security release of the channel, the version to move to.</param>
internal sealed record DotnetRuntimeAdvisory(
    string CveId, string? CveUrl, DotnetVersion FixedIn, string? FixedOn, DotnetVersion LatestSecurity);

/// <summary>Reads a channel's official release metadata
/// (<c>https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/&lt;major.minor&gt;/releases.json</c>). Each release
/// carries <c>security</c> and the <c>cve-list</c> it fixes, published the day it ships.
///
/// A release's list is not the list of the CVEs that release introduced the fix of: 10.0.14 repeats two CVEs that
/// 10.0.13 had already fixed. The release that fixes a CVE is therefore the OLDEST one that lists it, and an installed
/// runtime is exposed to a CVE only when that release is newer than the runtime.</summary>
internal static class DotnetReleaseAdvisories
{
    /// <summary>The CVEs fixed after <paramref name="installed"/>, ordered by identifier. Metadata that is not the
    /// channel's, or that has no release list, is refused rather than read as "nothing to report".</summary>
    public static IReadOnlyList<DotnetRuntimeAdvisory> Affecting(DotnetVersion installed, string releasesJson)
    {
        ArgumentNullException.ThrowIfNull(installed);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(releasesJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The release metadata of .NET {installed.Channel} is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || ReadString(root, "channel-version") != installed.Channel
                || !root.TryGetProperty("releases", out var releases)
                || releases.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"The release metadata is not the release list of the .NET {installed.Channel} channel.");

            var fixedBy = new Dictionary<string, (DotnetVersion Version, string? Date, string? Url)>(StringComparer.OrdinalIgnoreCase);
            DotnetVersion? latestSecurity = null;
            foreach (var release in releases.EnumerateArray())
                latestSecurity = ReadRelease(release, installed, fixedBy, latestSecurity);

            return fixedBy
                .Where(entry => entry.Value.Version > installed)
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new DotnetRuntimeAdvisory(
                    entry.Key, entry.Value.Url, entry.Value.Version, entry.Value.Date, latestSecurity ?? entry.Value.Version))
                .ToList();
        }
    }

    private static DotnetVersion? ReadRelease(
        JsonElement release,
        DotnetVersion installed,
        Dictionary<string, (DotnetVersion Version, string? Date, string? Url)> fixedBy,
        DotnetVersion? latestSecurity)
    {
        var text = ReadString(release, "release-version");
        var version = DotnetVersion.TryParse(text)
            ?? throw new InvalidDataException($"The release metadata of .NET {installed.Channel} lists a release '{text}' that is not a version.");
        var isSecurity = release.TryGetProperty("security", out var security) && security.ValueKind == JsonValueKind.True;
        if (isSecurity && (latestSecurity is null || version > latestSecurity))
            latestSecurity = version;

        if (!release.TryGetProperty("cve-list", out var cves) || cves.ValueKind != JsonValueKind.Array)
            return latestSecurity;
        foreach (var cve in cves.EnumerateArray())
        {
            var id = ReadString(cve, "cve-id");
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException($"Release {version} of the release metadata lists a CVE without an identifier.");
            if (!fixedBy.TryGetValue(id, out var known) || version < known.Version)
                fixedBy[id] = (version, ReadString(release, "release-date"), ReadString(cve, "cve-url"));
        }
        return latestSecurity;
    }

    /// <summary>The metadata address of a channel under the base URL.</summary>
    public static Uri ChannelUrl(string baseUrl, DotnetVersion version) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{baseUrl.TrimEnd('/')}/{version.Channel}/releases.json"));

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
