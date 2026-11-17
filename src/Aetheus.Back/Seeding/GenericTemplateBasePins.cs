// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Seeding;

/// <summary>
/// R-13: the content a shipped generic template is seeded with when it extends another shipped generic template.
///
/// A generic file is seeded as a new version each time it changes, so its version number differs from one instance
/// to the next, while the file can only name a fixed one (<c>extends: application-deploy-prod-core@1</c>). Read as
/// written, an extending template would keep the base's first version on every instance that already had it, and a
/// change of the base (the R-13 migrate stage of the deploy-prod core) would never reach a deployment. The pin is
/// therefore rewritten to the version the base holds on this instance once seeded: the extending template then gains
/// a new version of its own, which the pipelines pinned on its previous one adopt through the fleet update and its
/// preview (R-2), never silently. A fresh instance seeds the base as v1, so the file is stored as written.
/// </summary>
internal sealed class GenericTemplateBasePins(IReadOnlyDictionary<string, PipelineTemplate> existing)
{
    private static readonly Regex ExtendsPin = new(
        @"^(?<head>[ \t]*extends[ \t]*:[ \t]*['""]?(?<name>[^@'""#\r\n]+)@)(?<version>\d+)",
        RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly Dictionary<string, (string Yaml, string? Note)> _desired = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);

    /// <summary>The content the generic template <paramref name="name"/> must hold as its latest version.</summary>
    internal string DesiredYaml(string name) => Resolve(name).Yaml;

    /// <summary>The changelog sentence naming the rewritten pin, or an empty string when the file is stored as written.</summary>
    internal string ChangelogNote(string name) =>
        Resolve(name).Note is { } note ? " " + note : string.Empty;

    /// <summary>
    /// The version the generic template <paramref name="name"/> holds once seeded: 1 when it is new, its latest when
    /// that already stores the desired content, the next one otherwise (the seeder appends it).
    /// </summary>
    internal int VersionAfterSeed(string name)
    {
        var desired = DesiredYaml(name);
        if (!existing.TryGetValue(name, out var template)) return 1;
        var latest = template.Versions.OrderByDescending(version => version.Version).FirstOrDefault();
        if (latest is not null && string.Equals(Normalize(latest.YamlContent), Normalize(desired), StringComparison.Ordinal))
            return latest.Version;
        return Math.Max(template.LatestVersion,
            template.Versions.Select(version => version.Version).DefaultIfEmpty(0).Max()) + 1;
    }

    internal static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private (string Yaml, string? Note) Resolve(string name)
    {
        if (_desired.TryGetValue(name, out var cached)) return cached;
        if (!_resolving.Add(name))
            throw new InvalidOperationException($"The shipped generic templates extend each other in a cycle at '{name}'.");
        var raw = DeliveryTemplateCatalog.ReadResource(DeliveryTemplateCatalog.Definitions[name].Resources.Single());
        string? note = null;
        var yaml = ExtendsPin.Replace(raw, match =>
        {
            var baseName = match.Groups["name"].Value.Trim();
            if (!IsShippedGeneric(baseName)) return match.Value;
            var version = VersionAfterSeed(baseName);
            if (version.ToString(System.Globalization.CultureInfo.InvariantCulture) != match.Groups["version"].Value)
                note = $"Extends {baseName}@{version}, the current version of its base template.";
            return match.Groups["head"].Value + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }, 1);
        _resolving.Remove(name);
        var resolved = (yaml, note);
        _desired[name] = resolved;
        return resolved;
    }

    private static bool IsShippedGeneric(string name) =>
        DeliveryTemplateCatalog.Definitions.TryGetValue(name, out var definition) && DeliveryTemplateCatalog.IsGeneric(definition.Resources);
}
