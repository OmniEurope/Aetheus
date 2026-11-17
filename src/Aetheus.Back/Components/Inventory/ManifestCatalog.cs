// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Inventory;

internal enum ManifestKind
{
    PackagesLock,
    CentralPackages,
    DotnetProject,
    GlobalJson,
    NpmLock,
    PnpmLock,
    YarnLock,
    PackageJson,
    Nvmrc,
    ComposerLock,
    ComposerJson,
    PythonRequirements,
    PythonProject,
    PoetryLock,
    UvLock,
    CargoLock,
    MavenPom,
    GoMod,
    Dockerfile,
    Gradle,
    RubyLock
}

/// <summary>Recognizes the files an inventory reads, and those of a technology it does not read.</summary>
internal static class ManifestCatalog
{
    /// <summary>Folders of installed or generated code: what they hold is stated by a manifest elsewhere.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.Ordinal)
    {
        "node_modules", "bower_components", "vendor", "bin", "obj", "dist", ".git", ".venv", "venv", "__pycache__", "target"
    };

    private static readonly Dictionary<string, ManifestKind> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["packages.lock.json"] = ManifestKind.PackagesLock,
        ["Directory.Packages.props"] = ManifestKind.CentralPackages,
        ["global.json"] = ManifestKind.GlobalJson,
        ["package-lock.json"] = ManifestKind.NpmLock,
        ["npm-shrinkwrap.json"] = ManifestKind.NpmLock,
        ["pnpm-lock.yaml"] = ManifestKind.PnpmLock,
        ["yarn.lock"] = ManifestKind.YarnLock,
        ["package.json"] = ManifestKind.PackageJson,
        [".nvmrc"] = ManifestKind.Nvmrc,
        ["composer.lock"] = ManifestKind.ComposerLock,
        ["composer.json"] = ManifestKind.ComposerJson,
        ["pyproject.toml"] = ManifestKind.PythonProject,
        ["poetry.lock"] = ManifestKind.PoetryLock,
        ["uv.lock"] = ManifestKind.UvLock,
        ["Cargo.lock"] = ManifestKind.CargoLock,
        ["pom.xml"] = ManifestKind.MavenPom,
        ["go.mod"] = ManifestKind.GoMod,
        ["build.gradle"] = ManifestKind.Gradle,
        ["build.gradle.kts"] = ManifestKind.Gradle,
        ["Gemfile.lock"] = ManifestKind.RubyLock
    };

    public static ManifestKind? Classify(string path)
    {
        var segments = path.Split('/');
        if (segments[..^1].Any(SkippedFolders.Contains)) return null;
        var name = segments[^1];
        if (ByFileName.TryGetValue(name, out var kind)) return kind;
        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            return ManifestKind.DotnetProject;
        if (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return ManifestKind.PythonRequirements;
        return IsDockerfile(name) ? ManifestKind.Dockerfile : null;
    }

    public static string Technology(ManifestKind kind) => kind switch
    {
        ManifestKind.PackagesLock or ManifestKind.CentralPackages or ManifestKind.DotnetProject or ManifestKind.GlobalJson => "dotnet",
        ManifestKind.NpmLock or ManifestKind.PnpmLock or ManifestKind.YarnLock or ManifestKind.PackageJson or ManifestKind.Nvmrc => "javascript",
        ManifestKind.ComposerLock or ManifestKind.ComposerJson => "php",
        ManifestKind.PythonRequirements or ManifestKind.PythonProject or ManifestKind.PoetryLock or ManifestKind.UvLock => "python",
        ManifestKind.MavenPom or ManifestKind.Gradle => "java",
        ManifestKind.GoMod => "go",
        ManifestKind.CargoLock => "rust",
        ManifestKind.RubyLock => "ruby",
        _ => "docker"
    };

    private static bool IsDockerfile(string name) =>
        name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Containerfile", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase);
}
