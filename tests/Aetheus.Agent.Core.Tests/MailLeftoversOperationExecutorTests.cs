// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R3-23 / R3-26: the agent reads what a mail stack left (packages, paths, marker,
/// ports) without privilege, and never calls a host clean on a partial read.</summary>
public class MailLeftoversOperationExecutorTests
{
    private static MailLeftoversOperationExecutor Build(
        IReadOnlyList<string> dpkgLines,
        int dpkgExitCode = 0,
        bool dpkgTimedOut = false,
        IReadOnlyList<string>? existingPaths = null,
        List<ObservedPortDto>? listening = null)
    {
        var ports = Substitute.For<IListeningPortsCollector>();
        ports.CollectAsync(Arg.Any<CancellationToken>()).Returns(listening ?? []);
        return new MailLeftoversOperationExecutor(ports, NullLogger<MailLeftoversOperationExecutor>.Instance)
        {
            IsLinux = true,
            PathExists = path => existingPaths?.Contains(path) == true,
            RunDpkgQueryAsync = (_, _) => Task.FromResult<(ExecutorResult, IReadOnlyList<string>)>(
                (new ExecutorResult(dpkgExitCode, dpkgTimedOut), dpkgLines))
        };
    }

    private static async Task<(ExecutorResult Result, List<string> Lines)> RunAsync(MailLeftoversOperationExecutor executor)
    {
        var lines = new List<string>();
        var result = await executor.ExecuteAsync(OperationKind.MailLeftoversCheck, "-", 120,
            (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        return (result, lines);
    }

    [Fact]
    public async Task TheReferenceLeftoversOf2026_10_12_AreAllReported_ThenCleanNo()
    {
        var executor = Build(
            ["ii \topendkim", "ii \topendkim-tools", "ii \tlibopendkim11", "ii \trspamd", "rc \tdovecot-core", "un \tpostfix",
             "dpkg-query: no packages found matching postfix-pcre"],
            dpkgExitCode: 1,
            existingPaths: ["/etc/opendkim", "/etc/rspamd", "/etc/dovecot", MailLeftovers.Marker],
            listening:
            [
                new() { Port = 8891, Protocol = "tcp", Holder = "opendkim" },
                new() { Port = 11332, Protocol = "tcp", Holder = "rspamd" },
                new() { Port = 11334, Protocol = "tcp", Holder = "rspamd" },
                new() { Port = 443, Protocol = "tcp", Holder = "apache2" },
                new() { Port = 25, Protocol = "udp", Holder = "other" }
            ]);

        var (result, lines) = await RunAsync(executor);

        Assert.Equal(0, result.ExitCode);
        var report = MailTaskOutputParser.ParseLeftovers(string.Join('\n', lines));
        Assert.True(report.IsComplete);
        Assert.False(report.IsClean);
        Assert.Equal(["opendkim", "opendkim-tools", "libopendkim11", "rspamd", "dovecot-core"],
            report.Items.Where(item => item.Kind == MailLeftovers.KindPackage).Select(item => item.Name).ToArray());
        Assert.Equal("rc", report.Items.Single(item => item.Name == "dovecot-core").Detail.Trim());
        Assert.Equal(["/etc/dovecot", "/etc/opendkim", "/etc/rspamd"],
            report.Items.Where(item => item.Kind == MailLeftovers.KindPath).Select(item => item.Name).ToArray());
        Assert.Single(report.Items, item => item.Kind == MailLeftovers.KindMarker);
        Assert.Equal(["8891", "11332", "11334"],
            report.Items.Where(item => item.Kind == MailLeftovers.KindPort).Select(item => item.Name).ToArray());
        Assert.Equal("CLEAN\tno", lines[^1]);
    }

    [Fact]
    public async Task ANothingLeftHost_EndsWithCleanYes()
    {
        var executor = Build(["un \tpostfix", "pn \tdovecot-core", "dpkg-query: no packages found matching rspamd*"], dpkgExitCode: 1);

        var (result, lines) = await RunAsync(executor);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["CLEAN\tyes"], lines);
        Assert.True(MailTaskOutputParser.ParseLeftovers(string.Join('\n', lines)).IsClean);
    }

    [Theory]
    [InlineData("iF ")] // half-configured
    [InlineData("iU ")] // unpacked
    [InlineData("hi ")] // hold, installed
    [InlineData("iH ")] // half-installed
    [InlineData("iW ")] // triggers-awaited
    [InlineData("iT ")] // triggers-pending
    [InlineData("iUR")] // unpacked, reinstallation required
    public async Task APackageInAnUnfinishedDpkgState_IsReportedAsALeftover(string state)
    {
        // dpkg writes the current status in capitals for these states; each still has files on disk.
        var executor = Build([$"{state}\trspamd", "un \tpostfix"], dpkgExitCode: 0);

        var (result, lines) = await RunAsync(executor);

        Assert.Equal(0, result.ExitCode);
        var report = MailTaskOutputParser.ParseLeftovers(string.Join('\n', lines));
        var package = Assert.Single(report.Items, item => item.Kind == MailLeftovers.KindPackage);
        Assert.Equal("rspamd", package.Name);
        Assert.Equal(state.Trim(), package.Detail.Trim());
        Assert.Equal("CLEAN\tno", lines[^1]);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task AnUnreadablePackageList_FailsWithoutTheCleanLine(int exitCode, bool timedOut)
    {
        var executor = Build(["sudo: dpkg-query: command not found"], exitCode, timedOut);

        var (result, lines) = await RunAsync(executor);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(lines, line => line.StartsWith("CLEAN", StringComparison.Ordinal));
        Assert.False(MailTaskOutputParser.ParseLeftovers(string.Join('\n', lines)).IsComplete);
    }

    [Fact]
    public async Task UnreadablePorts_FailWithoutTheCleanLine()
    {
        var ports = Substitute.For<IListeningPortsCollector>();
        ports.CollectAsync(Arg.Any<CancellationToken>()).Returns<Task<List<ObservedPortDto>>>(_ => throw new InvalidOperationException("ss missing"));
        var executor = new MailLeftoversOperationExecutor(ports, NullLogger<MailLeftoversOperationExecutor>.Instance)
        {
            IsLinux = true,
            PathExists = _ => false,
            RunDpkgQueryAsync = (_, _) => Task.FromResult<(ExecutorResult, IReadOnlyList<string>)>((new ExecutorResult(0, false), []))
        };

        var (result, lines) = await RunAsync(executor);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(lines, line => line.StartsWith("CLEAN", StringComparison.Ordinal));
    }

    [Fact]
    public void TheQuery_ListsTheMailPackagePatterns_WithTheirDpkgState()
    {
        var argv = MailLeftoversOperationExecutor.DpkgArgv();

        Assert.Equal("-W", argv[0]);
        Assert.Equal("-f=${db:Status-Abbrev}\t${Package}\n", argv[1]);
        Assert.Equal(MailLeftovers.PackagePatterns, argv.Skip(2).ToArray());
    }

    [Fact]
    public void TheKind_IsGatedByItsSoftwareCapability_AndTakesTheDashTarget()
    {
        Assert.True(Build([]).CanHandle(OperationKind.MailLeftoversCheck));
        Assert.Equal(AgentCapabilities.MailLeftoversCheck, AgentCapabilities.RequiredFor(OperationKind.MailLeftoversCheck));
        Assert.Contains(AgentCapabilities.MailLeftoversCheck, AgentCapabilities.SoftwareCapabilities);
        Assert.True(OperationTargetValidator.IsValid(OperationKind.MailLeftoversCheck, "-"));
        Assert.False(OperationTargetValidator.IsValid(OperationKind.MailLeftoversCheck, "/etc"));
    }
}
