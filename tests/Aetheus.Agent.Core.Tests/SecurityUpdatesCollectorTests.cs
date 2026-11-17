// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public class SecurityUpdatesCollectorTests
{
    private const string Sample = """
        Inst libssl3 [3.0.2-0ubuntu1.10] (3.0.2-0ubuntu1.12 Ubuntu:22.04/jammy-security [amd64])
        Inst vim [1.0] (1.1 Ubuntu:22.04/jammy-updates [amd64])
        """;

    private static SecurityUpdatesCollector Build(IShellRunner shell, bool aptPresent = true)
        => new(NullLogger<SecurityUpdatesCollector>.Instance, shell, _ => aptPresent);

    [Fact]
    public async Task NoApt_ReportsNotApplicable_NotZero()
    {
        var collector = Build(Substitute.For<IShellRunner>(), aptPresent: false);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.False(result.PackageManagerPresent);
        Assert.False(result.ProbeSucceeded);
    }

    [Fact]
    public async Task ProbeSucceeds_ReportsCounts()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("apt-get", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, Sample, ""));

        var result = await Build(shell).CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.PackageManagerPresent);
        Assert.True(result.ProbeSucceeded);
        Assert.Equal(2, result.PendingTotal);
        Assert.Equal(1, result.PendingSecurity); // libssl3 from jammy-security
    }

    [Fact]
    public async Task Probe_SimulatesWithNewPackages_SoAPendingKernelIsListed()
    {
        const string withKernel = """
            Inst linux-image-6.8.0-149-generic (6.8.0-149.149 Ubuntu:24.04/noble-security [amd64])
            Inst linux-generic [6.8.0-147.147] (6.8.0-149.149 Ubuntu:24.04/noble-security [amd64])
            """;
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("apt-get", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-s", "upgrade", "--with-new-pkgs" })),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, withKernel, ""));

        var result = await Build(shell).CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.ProbeSucceeded);
        Assert.Equal(2, result.PendingTotal);
        Assert.Contains(result.Updates, u => u.Package == "linux-image-6.8.0-149-generic");
    }

    [Fact]
    public async Task ProbeFails_ReportsUnknown_NotUpToDate()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("apt-get", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(100, "", "E: could not run"));

        var result = await Build(shell).CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.PackageManagerPresent);
        Assert.False(result.ProbeSucceeded); // UNKNOWN, never a fabricated "0 pending"
        Assert.Equal(0, result.PendingTotal);
    }

    [Fact]
    public async Task ProbeThrows_ReportsUnknown()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("apt-get", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns<ShellExecResult>(_ => throw new InvalidOperationException("boom"));

        var result = await Build(shell).CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.PackageManagerPresent);
        Assert.False(result.ProbeSucceeded);
    }
}
