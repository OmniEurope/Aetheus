// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Let jscpd use repository configuration without command-line lists that would replace
/// it. Add the node_modules exclusion only when no repository configuration exists.</summary>
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
