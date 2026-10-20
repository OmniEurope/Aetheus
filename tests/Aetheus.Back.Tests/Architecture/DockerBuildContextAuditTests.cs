// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Architecture;

public sealed class DockerBuildContextAuditTests
{
    [Fact]
    public void RootDockerIgnore_ExcludesHostDotnetBuildOutputs()
    {
        var root = FindRepoRoot();
        var rules = File.ReadAllLines(Path.Combine(root, ".dockerignore"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        Assert.Contains("**/bin", rules);
        Assert.Contains("**/obj", rules);
        Assert.DoesNotContain(rules, rule =>
            rule.StartsWith('!')
            && (rule.Contains("/bin", StringComparison.OrdinalIgnoreCase)
                || rule.Contains("/obj", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void VpsSimDockerfiles_DefaultToNonRootAndNeverUseImplicitSshPublication()
    {
        var root = FindRepoRoot();
        var ignore = File.ReadAllText(Path.Combine(root, ".trivyignore.yaml"));
        Assert.Contains("misconfigurations: []", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("AVD-DS-0002", ignore, StringComparison.Ordinal);

        var simulatorDockerfiles = new[]
        {
            File.ReadAllText(Path.Combine(root, "deploy", "docker", "Dockerfile.vpssim")),
            File.ReadAllText(Path.Combine(root, "deploy", "docker", "Dockerfile.vpssim-blank"))
        };
        Assert.All(simulatorDockerfiles, dockerfile =>
        {
            Assert.Contains("USER vpssim-runtime", dockerfile, StringComparison.Ordinal);
            Assert.DoesNotContain("EXPOSE 22", dockerfile, StringComparison.Ordinal);
        });

        var simulatorComposeFiles = new[]
        {
            File.ReadAllText(Path.Combine(root, "deploy", "compose", "vpssim.compose.yml")),
            File.ReadAllText(Path.Combine(root, "deploy", "compose", "vpssim-blank.compose.yml"))
        };
        Assert.All(simulatorComposeFiles, compose =>
        {
            Assert.Contains("user: \"0:0\"", compose, StringComparison.Ordinal);
            Assert.Contains("127.0.0.1:${VPSSIM_SSH_PORT:-2222}:22", compose, StringComparison.Ordinal);
        });

        var productionDefinitions = RepositoryScan.Enumerate(Path.Combine(root, "deploy"), "*")
            .Where(path => !Path.GetFileName(path).StartsWith("vpssim", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetExtension(path) is ".yaml" or ".yml" or ".conf" or ".sh")
            .Select(File.ReadAllText)
            .ToList();
        Assert.DoesNotContain(productionDefinitions, definition =>
            definition.Contains("Dockerfile.vpssim", StringComparison.Ordinal));
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
