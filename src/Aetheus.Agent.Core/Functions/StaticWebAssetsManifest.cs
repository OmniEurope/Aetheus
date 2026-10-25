// SPDX-License-Identifier: EUPL-1.2
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Port of the former <c>rebase-static-web-assets.mjs</c> script: a prebuilt test project's static web
/// assets runtime manifest names absolute content roots of the CI workspace that built it. Each root
/// must lie under the web project (the last <c>/&lt;project&gt;/</c> in it, compared ignoring case);
/// it is rebased onto the same path under the restored source tree, created, and written back with a
/// trailing separator, before <c>WebApplicationFactory</c> builds its file providers from it.
/// <para>
/// Addition to the script, which refused them: a root shaped like a NuGet package's static web assets
/// (<c>&lt;packages&gt;/&lt;id&gt;/&lt;version&gt;/staticwebassets/</c>, the OmniEurope.Blazor package
/// since the backend references it) names the CI runner's package cache. It is rebased onto the same
/// package in this host's cache, which must hold it (it is never created: a missing package is a
/// restore defect, not an empty folder). Any other root is still refused.
/// </para>
/// </summary>
internal static partial class StaticWebAssetsManifest
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"/(?<id>[A-Za-z0-9_][A-Za-z0-9_.-]*)/(?<version>[0-9][A-Za-z0-9_.+-]*)/staticwebassets/?$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageContentRoot();

    /// <summary>Rebases the manifest in place; the number of roots, or the refusal. <paramref name="packageRoot"/>
    /// is this host's NuGet package folder, or null when it is unknown (package roots are then refused).</summary>
    public static (int Count, string? Error) Rebase(string sourceRoot, string manifestPath, string project, string? packageRoot = null)
    {
        var resolvedSourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        if (manifest["ContentRoots"] is not JsonArray { Count: > 0 } roots)
            return (0, $"Static web assets manifest has no content roots: {manifestPath}");

        var marker = "/" + project.Replace('\\', '/').Trim('/') + "/";
        var rebased = new JsonArray();
        foreach (var root in roots)
        {
            var contentRoot = root?.GetValue<string>() ?? string.Empty;
            var (value, error) = RebaseRoot(resolvedSourceRoot, contentRoot, marker, packageRoot);
            if (error is not null)
                return (0, error);
            rebased.Add(value);
        }
        manifest["ContentRoots"] = rebased;
        File.WriteAllText(manifestPath, manifest.ToJsonString(Compact));
        return (rebased.Count, null);
    }

    private static (string? Value, string? Error) RebaseRoot(string sourceRoot, string contentRoot, string marker, string? packageRoot)
    {
        var normalized = contentRoot.Replace('\\', '/');
        var markerIndex = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return RebasePackageRoot(normalized, contentRoot, packageRoot);

        var relative = normalized[(markerIndex + 1)..];
        var rebased = Path.GetFullPath(Path.Combine([sourceRoot, .. relative.Split('/')]));
        if (!rebased.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return (null, $"Static web assets content root escapes the restored source: {contentRoot}");
        Directory.CreateDirectory(rebased);
        return (Path.TrimEndingDirectorySeparator(rebased) + Path.DirectorySeparatorChar, null);
    }

    private static (string? Value, string? Error) RebasePackageRoot(string normalized, string contentRoot, string? packageRoot)
    {
        var match = PackageContentRoot().Match(normalized);
        if (!match.Success || packageRoot is null)
            return (null, $"Unexpected static web assets content root: {contentRoot}");

        var rebased = Path.Combine(
            Path.GetFullPath(packageRoot), match.Groups["id"].Value, match.Groups["version"].Value, "staticwebassets");
        if (!Directory.Exists(rebased))
            return (null, $"Static web assets package content root is not in this host's NuGet cache: {contentRoot} (looked for {rebased})");
        return (rebased + Path.DirectorySeparatorChar, null);
    }
}
