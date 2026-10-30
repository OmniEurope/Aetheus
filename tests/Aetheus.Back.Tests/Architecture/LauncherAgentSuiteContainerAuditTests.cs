// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Candidate 2961's CI failed an agent unit test that <c>ylaunch -ta</c> had passed on Windows: the test read the real
/// <c>/etc/apache2</c> of the Linux CI agent. Several agent tests read the machine they run on (the system
/// configuration, file modes, POSIX shells), so the launcher now runs the agent suite in a Linux container of the SDK
/// image the release builds use, the way the CI runs it (owner decision of 2026-10-27), with no silent fallback to
/// the host.
///
/// Like the other launcher guards, this reads the script rather than running it.
/// </summary>
public sealed class LauncherAgentSuiteContainerAuditTests
{
    private static string LauncherSource =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "scripts", "launch-core.ps1")).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void AgentSuite_RunsInALinuxContainerBuiltInRelease()
    {
        var source = LauncherSource;

        Assert.Contains("Kind = \"agent-container\"", source, StringComparison.Ordinal);
        Assert.Contains("Start-AgentSuiteContainerRun -Label $Suite.Title", source, StringComparison.Ordinal);
        var runner = Between(source, "$agentSuiteRunnerScript = @'", "'@");
        Assert.Contains("dotnet test --project tests/Aetheus.Agent.Core.Tests --configuration Release", runner, StringComparison.Ordinal);
        // The checkout is mounted read-only: the container's build never touches the host's Windows bin/obj.
        Assert.Contains("type=bind,src=$root,dst=/src,readonly", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSuite_NeverRunsOnTheHost()
    {
        var source = LauncherSource;

        Assert.DoesNotContain("Target = (Join-Path $root \"tests\\Aetheus.Agent.Core.Tests\")", source, StringComparison.Ordinal);
        var starter = Between(source, "function Start-AgentSuiteContainerRun", "\n}\n");
        Assert.DoesNotContain("Start-TestRun", starter, StringComparison.Ordinal);
        // Every way the container can fail to start is a failed suite, never a host run.
        Assert.True(Regex.Matches(starter, "return New-UnlaunchedAgentSuiteRun").Count >= 4,
            "Start-AgentSuiteContainerRun must report each start failure (docker, image, create, copy) as a failed suite.");
        var unlaunched = Between(source, "function New-UnlaunchedAgentSuiteRun", "\n}\n");
        Assert.Contains("ExitCode = 1", unlaunched, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSuiteImage_IsTheDigestPinnedSdkOfTheReleaseBuild()
    {
        var source = LauncherSource;
        Assert.Contains("deploy\\docker\\Dockerfile.back", source, StringComparison.Ordinal);
        Assert.Contains("AS\\s+build-source", source, StringComparison.Ordinal);

        // The launcher reads its image from that stage: it must exist, and stay pinned by digest.
        var dockerfile = File.ReadAllText(Path.Combine(RepositoryScan.Root, "deploy", "docker", "Dockerfile.back"));
        Assert.Matches(
            new Regex(@"^FROM\s+mcr\.microsoft\.com/dotnet/sdk:\S+@sha256:[0-9a-f]{64}\s+AS\s+build-source\s*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant),
            dockerfile);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found in the launcher.");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}' in the launcher.");
        return source[from..to];
    }
}
