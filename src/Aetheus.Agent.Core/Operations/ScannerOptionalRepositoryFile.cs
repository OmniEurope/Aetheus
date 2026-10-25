// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The repository files a scanner reads only when the repository has them, so that no file is required in the
/// project (ADR-050). Gitleaks reads its rules from <c>.gitleaks.toml</c> when present and its built-in rules
/// otherwise, which is what the two-line default file (<c>[extend]</c>, <c>useDefault = true</c>) asked for
/// anyway (recette R3-63, option 3). Trivy IaC reads its exceptions from <c>.trivyignore.yaml</c> when present
/// and reports every misconfiguration otherwise. The manifest names each file with its option; both scanners
/// refuse a path that does not exist, so the pair is dropped when the file is absent from the scanned source.
/// </summary>
internal static class ScannerOptionalRepositoryFile
{
    /// <summary>Where the scanner container sees the repository.</summary>
    private const string ContainerSourceRoot = "/src/";

    /// <summary>The option that names the optional repository file of <paramref name="scanner"/>, or null when
    /// the scanner has none.</summary>
    internal static string? Option(ScannerManifestEntry scanner) => scanner.Key switch
    {
        "gitleaks" or "gitleaks-history" => "--config",
        "trivy-iac" => "--ignorefile",
        _ => null
    };

    /// <summary>Removes <c><paramref name="option"/> /src/&lt;path&gt;</c> from <paramref name="arguments"/> when
    /// that path is not a file of <paramref name="sourceDirectory"/>. Returns whether it removed it.</summary>
    internal static bool DropAbsent(List<string> arguments, string option, string sourceDirectory)
    {
        var index = arguments.IndexOf(option);
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
