// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Candidate 347's QA failed 17 release tests that <c>ylaunch -ta</c> had passed on Windows (a stand-in <c>docker</c>
/// written without its execute bit, which Windows cannot see). <c>ylaunch -tl</c> therefore runs the full suite in a
/// resource-limited Linux container of the SDK image the release builds pin, in Release with the analyzers on, before a
/// candidate (owner decision of 2026-11-12). It is run by the agent, never by <c>-ta</c> or the pre-push hook, and never
/// falls back to the host. Like the other launcher guards, this reads the scripts rather than running them.
/// </summary>
public sealed class LauncherLinuxSuiteAuditTests
{
    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepositoryScan.Root, .. path])).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void LinuxSuite_IsNotPartOfTaTOrThePrePushHook()
    {
        var core = Read("scripts", "launch-core.ps1");

        // The only use of the switch is the block that runs the Linux suite and exits: -ta, -t and -c never imply it.
        Assert.Single(Regex.Matches(core, @"\$TestLinux\b"));
        Assert.Contains("if ($TestLinux) {", core, StringComparison.Ordinal);

        var hooks = Read(".husky", "task-runner.json");
        Assert.DoesNotContain("-tl", hooks, StringComparison.Ordinal);
        Assert.DoesNotContain("TestLinux", hooks, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxSuite_RunsEverySuiteInALimitedLinuxContainerBuiltInReleaseWithAnalyzers()
    {
        var source = Read("scripts", "linux-suite.ps1");

        foreach (var project in new[]
                 {
                     "tests/Aetheus.Analyzers.Tests", "tests/Aetheus.Agent.Core.Tests", "tests/Aetheus.Telemetry.Tests",
                     "tests/Aetheus.WebAnalytics.Tests", "tests/Aetheus.Front.Tests", "tests/Aetheus.Back.Tests",
                     "tests/Aetheus.Back.IntegrationTests",
                 })
        {
            Assert.Contains(project, source, StringComparison.Ordinal);
        }

        var runner = Between(source, "$linuxSuiteRunnerScript = @'", "'@");
        Assert.Contains("dotnet test --project \"$PROJECT\" --configuration Release", runner, StringComparison.Ordinal);
        // The analyzers are part of the suite: a build that switches them off proves less than the CI does.
        Assert.DoesNotContain("RunAnalyzers=false", source, StringComparison.Ordinal);
        // CPU and memory limits on the container, the checkout mounted read-only, Testcontainers on the engine socket.
        Assert.Contains("\"--cpus\"", source, StringComparison.Ordinal);
        Assert.Contains("\"--memory\"", source, StringComparison.Ordinal);
        Assert.Contains("type=bind,src=$root,dst=/src,readonly", source, StringComparison.Ordinal);
        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", source, StringComparison.Ordinal);
        Assert.Contains("aetheus-linux-suite-nuget", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxSuite_NeverRunsOnTheHostAndUsesTheDigestPinnedSdkImage()
    {
        var source = Read("scripts", "linux-suite.ps1");

        Assert.DoesNotContain("Start-TestRun", source, StringComparison.Ordinal);
        Assert.DoesNotContain("& dotnet test", source, StringComparison.Ordinal);
        var invoker = Between(source, "function Invoke-LinuxSuite", "\n}\n");
        Assert.True(Regex.Matches(invoker, @"return 1\b").Count >= 4,
            "Invoke-LinuxSuite must report each start failure (Docker engine, image, suite name, memory) as a failure, never as a host run.");

        Assert.Contains("deploy\\docker\\Dockerfile.back", source, StringComparison.Ordinal);
        Assert.Contains("AS\\s+build-source", source, StringComparison.Ordinal);
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
