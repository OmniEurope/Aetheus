// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Recette R-12: the virtual hosts of the sites-available files that Apache does not load (no sites-enabled
/// entry names them or links to them). Read-only: the files are parsed, never sourced or executed.
/// </summary>
internal static partial class ApacheDisabledSites
{
    /// <summary>A site file larger than this is not a hand-sized vhost: it is skipped, not read.</summary>
    internal const long MaxSiteFileBytes = 256 * 1024;

    /// <summary>At most this many disabled files are read per collection.</summary>
    internal const int MaxSiteFiles = 200;

    internal static IReadOnlyList<ApacheVirtualHostDto> Read(
        string sitesAvailableDirectory, string sitesEnabledDirectory, Func<string, string?> readLinkTarget)
    {
        var loaded = LoadedSiteNames(sitesEnabledDirectory, readLinkTarget);
        var result = new List<ApacheVirtualHostDto>();
        var files = Directory.EnumerateFiles(sitesAvailableDirectory, "*.conf")
            .Where(file => OperationTargetValidator.ApacheSiteFileRegex().IsMatch(Path.GetFileName(file)))
            .Where(file => !loaded.Contains(Path.GetFileName(file)))
            .Order(StringComparer.Ordinal)
            .Take(MaxSiteFiles);
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null || info.Length > MaxSiteFileBytes) continue;
            result.AddRange(Parse(file, File.ReadAllText(file)));
        }

        return result;
    }

    /// <summary>The names of the sites-enabled entries and of the files they link to.</summary>
    private static HashSet<string> LoadedSiteNames(string sitesEnabledDirectory, Func<string, string?> readLinkTarget)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sitesEnabledDirectory))
        {
            names.Add(Path.GetFileName(entry));
            if (readLinkTarget(entry) is { Length: > 0 } target)
                names.Add(Path.GetFileName(target));
        }

        return names;
    }

    /// <summary>One disabled virtual host per <c>&lt;VirtualHost&gt;</c> block of <paramref name="content"/>.</summary>
    internal static IEnumerable<ApacheVirtualHostDto> Parse(string configFile, string content)
    {
        var code = CommentLine().Replace(content, string.Empty);
        foreach (Match block in VirtualHostBlock().Matches(code))
        {
            var body = block.Groups[2].Value;
            yield return new ApacheVirtualHostDto
            {
                ServerName = ServerName().Match(body) is { Success: true } name ? name.Groups[1].Value : string.Empty,
                Port = Port(block.Groups[1].Value),
                DocumentRoot = DocumentRoot().Match(body) is { Success: true } root ? root.Groups[1].Value : string.Empty,
                ConfigFile = configFile,
                IsEnabled = false
            };
        }
    }

    // "<VirtualHost *:443>", "<VirtualHost 10.0.0.1:8080 [::1]:8080>", "<VirtualHost _default_>" (port 80).
    private static int Port(string addresses)
    {
        var first = addresses.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var colon = first.LastIndexOf(':');
        return colon >= 0 && int.TryParse(first[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? port
            : 80;
    }

    [GeneratedRegex(@"^\s*#.*$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommentLine();

    [GeneratedRegex(@"<VirtualHost\s+([^>]*)>(.*?)</VirtualHost>", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VirtualHostBlock();

    [GeneratedRegex(@"^\s*ServerName\s+(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ServerName();

    [GeneratedRegex(@"^\s*DocumentRoot\s+[""']?([^""'\s]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DocumentRoot();
}
