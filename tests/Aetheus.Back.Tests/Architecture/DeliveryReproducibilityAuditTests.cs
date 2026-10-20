// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Security pins of the delivery (image digests, action SHAs, installer hashes, unprivileged fixtures).
/// The consistency rules that used to live here (SDK and runtime versions against global.json and
/// Directory.Build.props, lock files and locked restores, runner generation) are checked before every
/// push by scripts/check-delivery-config.ps1 (PLAN-002 lot 5, AUD-08).
/// </summary>
public sealed partial class DeliveryReproducibilityAuditTests
{
    private const string InstallerCommit = "da3ce11ba63f3dbb0fb835d41bda2665d5c48e84";
    private const string InstallerSha256 = "082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e";
    private static string Root => FindRepoRoot();
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    [GeneratedRegex(@"(?m)^\s*(?:FROM|image:)\s+(?<image>(?:mcr\.microsoft\.com/|ubuntu:|postgres:)[^\s]+)")]
    private static partial Regex ExternalImageRegex();

    [Fact]
    public void LocalFixtures_MinimizePrivilegeAndKeepSshPasswordsOutOfImageLayers()
    {
        var e2eDockerfile = Read("deploy", "docker", "Dockerfile.e2e");
        var totoDockerfile = Read("deploy", "pipelines", "toto-e2e-fixture", "Dockerfile");
        Assert.Contains("USER pwuser", e2eDockerfile, StringComparison.Ordinal);
        Assert.Contains("USER app", totoDockerfile, StringComparison.Ordinal);

        var vpsDockerfiles = Read("deploy", "docker", "Dockerfile.vpssim")
                             + Read("deploy", "docker", "Dockerfile.vpssim-blank");
        var vpsCompose = Read("deploy", "compose", "vpssim.compose.yml")
                         + Read("deploy", "compose", "vpssim-blank.compose.yml");
        Assert.DoesNotContain("ARG VPSSIM_ROOT_PASSWORD", vpsDockerfiles, StringComparison.Ordinal);
        Assert.DoesNotContain("VPSSIM_ROOT_PASSWORD:", vpsCompose, StringComparison.Ordinal);

        var launcher = Read("scripts", "launch-core.ps1");
        Assert.Contains("""docker exec -i aetheus-vpssim sh -c 'tr -d "\r" | chpasswd'""", launcher, StringComparison.Ordinal);
        Assert.Contains("$rootPassword = $null", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteDotnetInstallers_AreCommitPinnedAndHashVerified()
    {
        var candidates = RepositoryScan.Enumerate(Root, "*")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsNestedWorktree(path))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => new[] { ".sh", ".yaml", ".yml" }.Contains(Path.GetExtension(path), StringComparer.Ordinal)
                           || Path.GetFileName(path).StartsWith("Dockerfile", StringComparison.Ordinal))
            .Select(path => new { Path = path, Source = File.ReadAllText(path) })
            .Where(file => file.Source.Contains("dotnet-install.sh", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(candidates);
        Assert.All(candidates, file =>
        {
            Assert.DoesNotContain("https://dot.net/v1/dotnet-install.sh", file.Source, StringComparison.Ordinal);
            Assert.Contains(InstallerCommit, file.Source, StringComparison.Ordinal);
            Assert.Contains(InstallerSha256, file.Source, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sha256sum", file.Source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void FrontRuntimeConfiguration_IsWrittenAsJsonWithoutSedInterpolation()
    {
        var entrypoint = Read("deploy", "docker", "entrypoint-front.sh");
        var server = Read("deploy", "docker", "StaticServer.Program.cs");
        Assert.DoesNotMatch(@"(?m)^\s*sed\b", entrypoint);
        Assert.Contains("JsonNode.Parse", server, StringComparison.Ordinal);
        Assert.Contains("settings[\"ApiBaseUrl\"] = apiBaseUrl", server, StringComparison.Ordinal);
        Assert.Contains("appSettings[\"Version\"] = appVersion", server, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, settingsPath, overwrite: true)", server, StringComparison.Ordinal);
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;

    // Relative to Root, never on the absolute path: when the suite itself runs from a worktree, Root
    // already sits under .claude/worktrees/, so an absolute Contains() excluded every file in the
    // repository and left both scans below silently empty.
    // The project's own worktree folder (.worktrees) holds full copies of the repository too.
    private static bool IsNestedWorktree(string path) => Path.GetRelativePath(Root, path)
        .Replace('\\', '/') is var relative
        && (relative.StartsWith(".claude/worktrees/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(".worktrees/", StringComparison.OrdinalIgnoreCase));
}
