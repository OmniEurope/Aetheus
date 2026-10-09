// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Recette R2-120: the jscpd scanner reads the analysed repository's own configuration. jscpd 5
/// replaces (does not merge) a configuration list with any list given on the command line, so the
/// manifest passes no ignore option: a <c>--ignore-pattern</c> there once replaced the repository's
/// <c>ignorePattern</c> and kept reporting a duplicate the repository had excluded. A repository
/// without any jscpd configuration still must not have the installed packages it was projected with
/// scanned as its own code, so only then the agent adds the <c>node_modules</c> file ignore.
/// </summary>
internal static class JscpdRepositoryConfiguration
{
    internal const string IgnoreOption = "--ignore";
    internal const string NodeModulesGlob = "**/node_modules/**";

    /// <summary>The files jscpd 5 reads its configuration from when none is passed explicitly.</summary>
    private const string ConfigurationFileName = ".jscpd.json";
    private const string PackageFileName = "package.json";
    private const string PackageConfigurationProperty = "jscpd";
    private const long MaxPackageFileBytes = 4 * 1024 * 1024;

    internal static bool Applies(ScannerManifestEntry scanner) =>
        string.Equals(scanner.Key, "jscpd", StringComparison.Ordinal);

    /// <summary>The arguments the agent appends for this repository: none when it configures jscpd.</summary>
    internal static IReadOnlyList<string> AdditionalArguments(string sourceDirectory) =>
        HasConfiguration(sourceDirectory) ? [] : [IgnoreOption, NodeModulesGlob];

    internal static bool HasConfiguration(string sourceDirectory)
    {
        if (File.Exists(Path.Combine(sourceDirectory, ConfigurationFileName)))
            return true;
        var packageFile = new FileInfo(Path.Combine(sourceDirectory, PackageFileName));
        if (!packageFile.Exists || packageFile.Length > MaxPackageFileBytes)
            return false;
        try
        {
            using var stream = packageFile.OpenRead();
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(PackageConfigurationProperty, out _);
        }
        catch (JsonException)
        {
            // An unreadable package.json configures nothing jscpd could use: keep the default ignore.
            return false;
        }
    }
}
