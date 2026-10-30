// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// Recette R-97 (ADR-058): how a variable library names what a project deployed, read the same way for the
/// project being deleted and for every other project: the keys, <c>&lt;slug&gt;.share</c> merged under each library,
/// <c>$(NAME)</c> references expanded, and the hosts (<c>APP_HOST</c> / <c>API_HOST</c>, or the template's formula).
/// </summary>
internal static partial class ProjectServerCleanupLibraryValues
{
    internal const string ComposeProjectKey = "COMPOSE_PROJECT";
    internal const string StateDirectoryKey = "STATE_DIR";
    internal const string UpstreamConfigurationKey = "UPSTREAM_CONF";
    internal const string AppHostKey = "APP_HOST";
    internal const string ApiHostKey = "API_HOST";
    internal const string DomainKey = "DOMAIN";

    private static readonly (string HostKey, string SubdomainKey)[] s_hosts =
        [(AppHostKey, "APP_SUBDOMAIN"), (ApiHostKey, "API_SUBDOMAIN")];

    private const int MaxExpansionPasses = 8;

    /// <summary>Expands <c>$(NAME)</c> and <c>$(NAME:-)</c> references from the entries. Null when a reference
    /// stays unresolved (an undefined name without <c>:-</c>, a cycle, any other expression).</summary>
    internal static string? Expand(string value, IReadOnlyDictionary<string, string> entries)
    {
        var current = value;
        for (var pass = 0; pass < MaxExpansionPasses && current.Contains('$', StringComparison.Ordinal); pass++)
        {
            var unresolved = false;
            current = Reference().Replace(current, match =>
            {
                if (entries.TryGetValue(match.Groups["name"].Value, out var replacement)) return replacement;
                if (match.Groups["optional"].Success) return string.Empty;
                unresolved = true;
                return match.Value;
            });
            if (unresolved) return null;
        }
        return current.Contains('$', StringComparison.Ordinal) ? null : current.Trim();
    }

    /// <summary>The unexpanded host of each host key: <c>APP_HOST</c> / <c>API_HOST</c> as written, or the template's
    /// formula when <c>DOMAIN</c> is set: <c>$(HOST_PREFIX:-)$(SUB).$(DOMAIN)</c> with a subdomain, and, for the
    /// application only, <c>$(HOST_PREFIX:-)$(DOMAIN)</c> when the library defines <c>APP_SUBDOMAIN</c> empty (R-74: the
    /// template then adds no dot and the application answers on the domain itself). An absent subdomain names no host.</summary>
    internal static IEnumerable<(string HostKey, string? Raw)> RawHosts(IReadOnlyDictionary<string, string> values)
    {
        var hasDomain = !string.IsNullOrWhiteSpace(values.GetValueOrDefault(DomainKey));
        foreach (var (hostKey, subdomainKey) in s_hosts)
        {
            yield return (hostKey, values.TryGetValue(hostKey, out var explicitHost) ? explicitHost
                : !hasDomain ? null
                : !string.IsNullOrWhiteSpace(values.GetValueOrDefault(subdomainKey)) ? $"$(HOST_PREFIX:-)$({subdomainKey}).$({DomainKey})"
                : hostKey == AppHostKey && values.ContainsKey(subdomainKey) ? $"$(HOST_PREFIX:-)$({DomainKey})"
                : null);
        }
    }

    internal static bool IsHostKey(string key) => key is AppHostKey or ApiHostKey;

    /// <summary>The library's entries over the defaults (<c>&lt;slug&gt;.share</c>).</summary>
    internal static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string>? defaults, IReadOnlyDictionary<string, string> entries)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in defaults ?? new Dictionary<string, string>()) merged[key] = value;
        foreach (var (key, value) in entries) merged[key] = value;
        return merged;
    }

    [GeneratedRegex(@"\$\((?<name>[A-Za-z_][A-Za-z0-9_]*)(?<optional>:-)?\)")]
    private static partial Regex Reference();
}
