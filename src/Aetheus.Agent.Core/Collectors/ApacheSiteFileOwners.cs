// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Recette R-14: which site files the agent cannot delete, and whose they are. <c>sites-available</c> and
/// <c>sites-enabled</c> are sticky, so the agent can unlink only the entries its own account owns; a file created
/// by root or by certbot stays. The owner is read with one shell-free <c>stat</c>, and reported only when it is not
/// the agent's own account: an owner that cannot be read is never guessed.
/// </summary>
internal static partial class ApacheSiteFileOwners
{
    /// <summary>The <c>stat</c> format of a line: the owner's name, a space, the path.</summary>
    internal const string StatFormat = "%U %n";

    /// <summary>The distinct <c>sites-available</c> files, and the <c>sites-enabled</c> entries of the enabled ones, to
    /// read the owner of.</summary>
    internal static IReadOnlyList<string> PathsToRead(
        IEnumerable<ApacheVirtualHostDto> vhosts, string sitesAvailableDirectory, string sitesEnabledDirectory)
    {
        var paths = new List<string>();
        foreach (var vhost in vhosts)
        {
            if (SiteFile(vhost) is not { } file) continue;
            paths.Add(Join(sitesAvailableDirectory, file));
            if (vhost.IsEnabled)
                paths.Add(Join(sitesEnabledDirectory, file));
        }

        return [.. paths.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The owner of each path of a <c>stat</c> output of <see cref="StatFormat"/> (an absolute path, a drive letter
    /// allowed so the parser reads the same paths on a Windows test host); a line that is not "owner path" (an error, an unknown owner) is ignored.</summary>
    internal static IReadOnlyDictionary<string, string> Parse(string statOutput)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in statOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (OwnerLine().Match(line.Trim()) is not { Success: true } match) continue;
            var owner = match.Groups[1].Value;
            if (!string.Equals(owner, "UNKNOWN", StringComparison.Ordinal))
                owners[match.Groups[2].Value] = owner;
        }

        return owners;
    }

    /// <summary>Every virtual host whose file, or whose sites-enabled link, belongs to an account other than the
    /// agent's carries that owner; the others are returned untouched.</summary>
    internal static List<ApacheVirtualHostDto> Apply(
        IEnumerable<ApacheVirtualHostDto> vhosts, IReadOnlyDictionary<string, string> owners, string agentUser,
        string sitesAvailableDirectory, string sitesEnabledDirectory) =>
        [.. vhosts.Select(vhost =>
        {
            if (SiteFile(vhost) is not { } file) return vhost;
            var candidates = vhost.IsEnabled
                ? new[] { Join(sitesAvailableDirectory, file), Join(sitesEnabledDirectory, file) }
                : [Join(sitesAvailableDirectory, file)];
            var foreign = candidates
                .Select(path => owners.GetValueOrDefault(path))
                .FirstOrDefault(owner => owner is not null && !string.Equals(owner, agentUser, StringComparison.Ordinal));
            return foreign is null ? vhost : vhost with { ConfigFileForeignOwner = foreign };
        })];

    // The file name of a site, or null when it is not a name the agent's site operations accept.
    private static string? SiteFile(ApacheVirtualHostDto vhost)
    {
        // "path:line" from apache2ctl -S; only a numeric suffix is a line, so a drive letter's colon stays in the path.
        var colon = vhost.ConfigFile.LastIndexOf(':');
        var path = colon > 0 && colon < vhost.ConfigFile.Length - 1 && vhost.ConfigFile[(colon + 1)..].All(char.IsAsciiDigit)
            ? vhost.ConfigFile[..colon]
            : vhost.ConfigFile;
        var file = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        return OperationTargetValidator.ApacheSiteFileRegex().IsMatch(file) ? file : null;
    }

    private static string Join(string directory, string file) => $"{directory.TrimEnd('/')}/{file}";

    [GeneratedRegex(@"^([A-Za-z0-9._-]{1,64}) ((?:[A-Za-z]:)?/[A-Za-z0-9._/-]+)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OwnerLine();
}
