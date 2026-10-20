// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R3-59: the agent lists every installed package without privilege (dpkg-query, rpm as the
/// fallback), and never presents a failed or empty read as the complete list.</summary>
public class PackageInventoryOperationExecutorTests
{
    private sealed record Query(string FileName, IReadOnlyList<string> Argv);

    private static PackageInventoryOperationExecutor Build(
        Func<string, (int ExitCode, bool TimedOut, IReadOnlyList<string> Lines)> answer,
        List<Query>? calls = null,
        bool isLinux = true) =>
        new(NullLogger<PackageInventoryOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            RunQueryAsync = (fileName, argv, _) =>
            {
                calls?.Add(new Query(fileName, argv));
                var (exitCode, timedOut, lines) = answer(fileName);
                return Task.FromResult<(ExecutorResult, IReadOnlyList<string>)>((new ExecutorResult(exitCode, timedOut), lines));
            }
        };

    private static async Task<(ExecutorResult Result, List<string> Lines)> RunAsync(PackageInventoryOperationExecutor executor, string target = "-")
    {
        var lines = new List<string>();
        var result = await executor.ExecuteAsync(OperationKind.PackageInventory, target, 120,
            (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        return (result, lines);
    }

    [Fact]
    public async Task Dpkg_ListsTheInstalledPackagesOnly_SortedWithTheirVersion_ThenTheTotal()
    {
        var calls = new List<Query>();
        var executor = Build(file => file == "dpkg-query"
            ? (0, false, ["ii \tmongodb-mongosh\t2.3.1", "ii \tcurl\t7.88.1-10+deb12u8", "rc \topendkim\t2.11.0",
                          "un \tpostfix\t", "iF \tlibc6:i386\t2.36-9", "[process] exit-code=0"])
            : throw new InvalidOperationException("rpm must not run when dpkg answered"), calls);

        var (result, lines) = await RunAsync(executor);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["PACKAGE\tcurl\t7.88.1-10+deb12u8", "PACKAGE\tlibc6:i386\t2.36-9", "PACKAGE\tmongodb-mongosh\t2.3.1", "TOTAL\t3"],
            lines);
        var query = Assert.Single(calls);
        Assert.Equal("dpkg-query", query.FileName);
        Assert.Equal(["-W", "-f=${db:Status-Abbrev}\t${binary:Package}\t${Version}\n"], query.Argv);

        var report = InstalledPackages.Parse(string.Join('\n', lines));
        Assert.True(report.IsComplete);
        Assert.Equal(["curl", "libc6:i386", "mongodb-mongosh"], report.Packages.Select(p => p.Name));
    }

    [Fact]
    public async Task WithoutDpkg_RpmIsRead()
    {
        var calls = new List<Query>();
        var executor = Build(file => file == "dpkg-query"
            ? (-1, false, ["Failed to launch 'dpkg-query': No such file or directory"])
            : (0, false, ["openssl\t3.0.7-27.el9", "bash\t5.1.8-9.el9", "[process] exit-code=0"]), calls);

        var (result, lines) = await RunAsync(executor);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["PACKAGE\tbash\t5.1.8-9.el9", "PACKAGE\topenssl\t3.0.7-27.el9", "TOTAL\t2"], lines);
        Assert.Equal(["dpkg-query", "rpm"], calls.Select(c => c.FileName));
        Assert.Equal(["-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\n"], calls[1].Argv);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public async Task NoDatabaseAnswers_TheTaskFails_WithoutATotalLine(int exitCode, bool timedOut)
    {
        var executor = Build(_ => (exitCode, timedOut, ["ii \tcurl\t7.88.1"]));

        var (result, lines) = await RunAsync(executor);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(lines, line => line.StartsWith("TOTAL", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("PACKAGE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyAnswer_IsAFailedRead_NotAHostWithNothingInstalled()
    {
        var executor = Build(_ => (0, false, ["[process] exit-code=0"]));

        var (result, lines) = await RunAsync(executor);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No package database could be read (dpkg-query, rpm)", lines);
    }

    [Fact]
    public async Task OutsideLinux_OrWithAnotherTarget_NothingRuns()
    {
        var calls = new List<Query>();

        var (windows, _) = await RunAsync(Build(_ => (0, false, ["ii \tcurl\t1"]), calls, isLinux: false));
        var (badTarget, _) = await RunAsync(Build(_ => (0, false, ["ii \tcurl\t1"]), calls), "/etc");

        Assert.Equal(-1, windows.ExitCode);
        Assert.Equal(-1, badTarget.ExitCode);
        Assert.Empty(calls);
    }

    [Fact]
    public void TheKind_IsGatedByItsSoftwareCapability_NotByThePackageGrant_AndTakesTheDashTarget()
    {
        var executor = Build(_ => (0, false, []));
        Assert.True(executor.CanHandle(OperationKind.PackageInventory));
        Assert.False(executor.CanHandle(OperationKind.ServiceUninstall));
        Assert.Equal(AgentCapabilities.PackageInventory, AgentCapabilities.RequiredFor(OperationKind.PackageInventory));
        Assert.Contains(AgentCapabilities.PackageInventory, AgentCapabilities.SoftwareCapabilities);
        Assert.True(OperationTargetValidator.IsValid(OperationKind.PackageInventory, "-"));
        Assert.False(OperationTargetValidator.IsValid(OperationKind.PackageInventory, "curl"));
    }

    [Fact]
    public void TheKindNumber_IsUnique()
    {
        Assert.Equal(213, (int)OperationKind.PackageInventory);
        Assert.Single(Enum.GetValues<OperationKind>(), kind => (int)kind == 213);
    }

    [Fact]
    public void Parse_IsIncomplete_WhenTheTotalIsMissing_OrDoesNotMatchTheLinesReceived()
    {
        Assert.False(InstalledPackages.Parse("PACKAGE\tcurl\t1\nPACKAGE\tbash\t2").IsComplete);
        Assert.False(InstalledPackages.Parse("PACKAGE\tcurl\t1\nTOTAL\t2").IsComplete);
        Assert.False(InstalledPackages.Parse(null).IsComplete);
        var complete = InstalledPackages.Parse("[agent] notice\r\nPACKAGE\tcurl\t1\r\nTOTAL\t1\r\n");
        Assert.True(complete.IsComplete);
        Assert.Equal(new InstalledPackageDto { Name = "curl", Version = "1" }, Assert.Single(complete.Packages));
    }
}
