// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Gitleaks reads its rules from the repository's <c>.gitleaks.toml</c> when the repository has one;
/// a repository without it is scanned with Gitleaks' built-in rules, which is what the two-line default
/// file (<c>[extend]</c>, <c>useDefault = true</c>) asked for anyway (recette R3-63, option 3: no file
/// is required in the project). The manifest names the file with <c>--config</c>; Gitleaks refuses a
/// <c>--config</c> path that does not exist, so the pair is dropped when the file is absent from the
/// scanned source.
/// </summary>
internal static class GitleaksRepositoryConfiguration
{
    internal const string ConfigOption = "--config";

    /// <summary>Where the scanner container sees the repository.</summary>
    private const string ContainerSourceRoot = "/src/";

    internal static bool Applies(ScannerManifestEntry scanner) =>
        scanner.Key is "gitleaks" or "gitleaks-history";

    /// <summary>Removes <c>--config /src/&lt;path&gt;</c> from <paramref name="arguments"/> when that
    /// path is not a file of <paramref name="sourceDirectory"/>. Returns whether it removed it.</summary>
    internal static bool DropAbsentConfiguration(List<string> arguments, string sourceDirectory)
    {
        var index = arguments.IndexOf(ConfigOption);
        if (index < 0 || index + 1 >= arguments.Count)
            return false;
        var containerPath = arguments[index + 1];
        if (!containerPath.StartsWith(ContainerSourceRoot, StringComparison.Ordinal))
            return false;
        var relative = containerPath[ContainerSourceRoot.Length..];
        if (File.Exists(Path.Combine(sourceDirectory, relative)))
            return false;
        arguments.RemoveRange(index, 2);
        return true;
    }
}
