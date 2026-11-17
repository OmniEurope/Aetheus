// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Let jscpd use repository configuration without command-line lists that would replace
/// it. jscpd 5 replaces a configured list with the same list given on the command line (verified for
/// <c>format</c> and <c>ignore</c>), so a default list is passed only where the repository defines none:
/// the node_modules and pipeline-directory exclusions when no configuration exists, and the source-language
/// restriction unless the configuration names its own <c>format</c>.</summary>
internal static class JscpdRepositoryConfiguration
{
    internal const string IgnoreOption = "--ignore";
    internal const string FormatOption = "--format";
    internal const string NodeModulesGlob = "**/node_modules/**";
    internal const string PipelineDefinitionsGlob = "**/.pipeline/**";

    /// <summary>The pipeline's own work directory: coverage and report files it writes beside the sources.</summary>
    internal const string PipelineQualityGlob = "**/.pipeline-quality/**";

    /// <summary>jscpd splits its <c>--ignore</c> value on commas.</summary>
    internal const string DefaultIgnore = NodeModulesGlob + "," + PipelineDefinitionsGlob + "," + PipelineQualityGlob;

    /// <summary>The jscpd formats of the languages the quality pipeline documents (C#, JavaScript with its
    /// mjs and cjs files, TypeScript, Python, Java). Without a restriction jscpd also measures every JSON, XML,
    /// markup, YAML or lock file of the repository, generated ones included, which is not source duplication.
    /// Comma-separated, as jscpd splits its <c>--format</c> value on commas.</summary>
    internal const string SourceFormats = "csharp,javascript,jsx,typescript,tsx,python,java";

    /// <summary>The files jscpd 5 reads its configuration from when none is passed explicitly.</summary>
    private const string ConfigurationFileName = ".jscpd.json";
    private const string PackageFileName = "package.json";
    private const string PackageConfigurationProperty = "jscpd";
    private const string FormatProperty = "format";
    private const long MaxPackageFileBytes = 4 * 1024 * 1024;

    private static readonly JsonDocumentOptions TolerantOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    internal static bool Applies(ScannerManifestEntry scanner) =>
        string.Equals(scanner.Key, "jscpd", StringComparison.Ordinal);

    /// <summary>The arguments the agent appends for this repository: no ignore list when it configures jscpd,
    /// and the source-language restriction unless its configuration names a <c>format</c> of its own.</summary>
    internal static IReadOnlyList<string> AdditionalArguments(string sourceDirectory)
    {
        var state = ReadConfiguration(sourceDirectory);
        List<string> arguments = [];
        if (!state.Present)
            arguments.AddRange([IgnoreOption, DefaultIgnore]);
        if (!state.DefinesFormat)
            arguments.AddRange([FormatOption, SourceFormats]);
        return arguments;
    }

    internal static bool HasConfiguration(string sourceDirectory) => ReadConfiguration(sourceDirectory).Present;

    private static ConfigurationState ReadConfiguration(string sourceDirectory)
    {
        var present = false;
        var definesFormat = false;
        var configurationFile = new FileInfo(Path.Combine(sourceDirectory, ConfigurationFileName));
        if (configurationFile.Exists)
        {
            present = true;
            // An unreadable .jscpd.json is the project's explicit file: never override what cannot be read.
            definesFormat |= !TryReadObject(configurationFile, out var configuration)
                || configuration.TryGetProperty(FormatProperty, out _);
        }
        var packageFile = new FileInfo(Path.Combine(sourceDirectory, PackageFileName));
        if (packageFile.Exists && packageFile.Length <= MaxPackageFileBytes
            && TryReadObject(packageFile, out var package)
            && package.TryGetProperty(PackageConfigurationProperty, out var packageConfiguration))
        {
            present = true;
            definesFormat |= packageConfiguration.ValueKind != JsonValueKind.Object
                || packageConfiguration.TryGetProperty(FormatProperty, out _);
        }
        return new ConfigurationState(present, definesFormat);
    }

    private static bool TryReadObject(FileInfo file, out JsonElement root)
    {
        root = default;
        try
        {
            using var stream = file.OpenRead();
            using var document = JsonDocument.Parse(stream, TolerantOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            // An unreadable package.json configures nothing jscpd could use: the caller keeps the defaults.
            return false;
        }
    }

    private readonly record struct ConfigurationState(bool Present, bool DefinesFormat);
}
