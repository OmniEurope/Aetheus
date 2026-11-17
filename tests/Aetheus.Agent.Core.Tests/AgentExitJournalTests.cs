// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public sealed class AgentExitJournalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 1, 8, 0, TimeSpan.Zero);
    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "aetheus-exit-journal-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
            Directory.Delete(_workDirectory, recursive: true);
    }

    private AgentExitJournal NewProcess(string? bootId = "boot-1") => new(
        Options.Create(new AetheusAgentOptions { WorkDirectory = _workDirectory }),
        _time,
        () => bootId,
        NullLogger<AgentExitJournal>.Instance);

    [Fact]
    public void FirstInstall_HasNothingToReport()
    {
        var journal = NewProcess();

        Assert.Null(journal.GetPendingReport());
        Assert.True(File.Exists(journal.RunningPath));
        Assert.False(File.Exists(journal.LastExitPath));
    }

    [Fact]
    public void RecordExit_WritesTheRecordAtomically()
    {
        var journal = NewProcess();
        _ = journal.GetPendingReport();

        journal.RecordExit(AgentExitKinds.Restart, "heartbeat loop stalled");

        Assert.True(File.Exists(journal.LastExitPath));
        Assert.False(File.Exists(journal.LastExitPath + ".tmp"));
        var text = File.ReadAllText(journal.LastExitPath);
        Assert.Contains("heartbeat loop stalled", text, StringComparison.Ordinal);
        Assert.Contains(AgentExitKinds.Restart, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Restarter_RecordsTheReasonBeforeEndingTheProcess()
    {
        var journal = NewProcess();
        _ = journal.GetPendingReport();
        var recordedWhenExiting = false;
        var restarter = new AgentProcessRestarter(
            journal, _ => recordedWhenExiting = File.Exists(journal.LastExitPath));

        restarter.Restart("Agent self-update applied; service-manager restart requested.");

        Assert.True(recordedWhenExiting);
        var next = NewProcess().GetPendingReport();
        Assert.NotNull(next);
        Assert.Equal(AgentExitKinds.Restart, next.Kind);
        Assert.Equal("Agent self-update applied; service-manager restart requested.", next.Reason);
        Assert.Equal(Now.UtcDateTime, next.OccurredAtUtc);
    }

    [Fact]
    public void NextStart_ReportsTheRecordedExitWithBothBootIds()
    {
        var first = NewProcess("boot-1");
        _ = first.GetPendingReport();
        first.RecordExit(AgentExitKinds.Stopped, "SIGTERM");

        var report = NewProcess("boot-2").GetPendingReport();

        Assert.NotNull(report);
        Assert.Equal(AgentExitKinds.Stopped, report.Kind);
        Assert.Equal("boot-1", report.PreviousBootId);
        Assert.Equal("boot-2", report.BootId);
    }

    [Fact]
    public void NextStart_WithoutExitRecord_ReportsAnUncleanExit()
    {
        var killed = NewProcess("boot-1");
        _ = killed.GetPendingReport();
        // The process is killed: no RecordExit ever runs.

        var report = NewProcess("boot-1").GetPendingReport();

        Assert.NotNull(report);
        Assert.Equal(AgentExitKinds.Unclean, report.Kind);
        Assert.Null(report.OccurredAtUtc);
        Assert.Equal("boot-1", report.PreviousBootId);
        Assert.Equal("boot-1", report.BootId);
        Assert.Contains("without writing one", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UncleanReport_SurvivesACrashBeforeItIsSent()
    {
        var killed = NewProcess();
        _ = killed.GetPendingReport();
        var second = NewProcess();
        _ = second.GetPendingReport();
        // The second process dies before its first heartbeat, with no record either.

        var report = NewProcess().GetPendingReport();

        Assert.NotNull(report);
        Assert.Equal(AgentExitKinds.Unclean, report.Kind);
    }

    [Fact]
    public void MarkReported_SendsTheReportOnce()
    {
        var first = NewProcess();
        _ = first.GetPendingReport();
        first.RecordExit(AgentExitKinds.Crash, "boom");
        var second = NewProcess();
        Assert.NotNull(second.GetPendingReport());

        second.MarkReported();

        Assert.Null(second.GetPendingReport());
        Assert.False(File.Exists(second.LastExitPath));
        // A third process that lived and ended cleanly leaves its own record, not the reported one.
        second.RecordExit(AgentExitKinds.Stopped, "stop");
        Assert.Equal(AgentExitKinds.Stopped, NewProcess().GetPendingReport()!.Kind);
    }

    [Fact]
    public void MarkReported_KeepsTheRecordThisProcessWroteMeanwhile()
    {
        var first = NewProcess();
        _ = first.GetPendingReport();
        first.RecordExit(AgentExitKinds.Crash, "boom");
        var second = NewProcess();
        Assert.NotNull(second.GetPendingReport());
        second.RecordExit(AgentExitKinds.Restart, "watchdog");

        second.MarkReported();

        Assert.Equal(AgentExitKinds.Restart, NewProcess().GetPendingReport()!.Kind);
    }

    [Fact]
    public void RecordExit_OnlyTheFirstCallOfAProcessCounts()
    {
        var journal = NewProcess();
        _ = journal.GetPendingReport();

        journal.RecordExit(AgentExitKinds.Restart, "watchdog");
        journal.RecordExit(AgentExitKinds.Stopped, "host stop that follows the restart");

        var report = NewProcess().GetPendingReport();
        Assert.Equal(AgentExitKinds.Restart, report!.Kind);
        Assert.Equal("watchdog", report.Reason);
    }

    [Fact]
    public void RecordExit_TruncatesALongReason()
    {
        var journal = NewProcess();
        _ = journal.GetPendingReport();

        journal.RecordExit(AgentExitKinds.Crash, new string('x', AgentExitReportDto.MaxReasonLength + 200));

        var report = NewProcess().GetPendingReport();
        Assert.Equal(AgentExitReportDto.MaxReasonLength, report!.Reason!.Length);
    }

    [Fact]
    public void NextStart_WithACorruptExitRecord_FallsBackToUnclean()
    {
        var first = NewProcess();
        _ = first.GetPendingReport();
        File.WriteAllText(first.LastExitPath, "{ not json");

        var report = NewProcess().GetPendingReport();

        Assert.NotNull(report);
        Assert.Equal(AgentExitKinds.Unclean, report.Kind);
    }

    [Fact]
    public async Task Recorder_RecordsAHostStopAsStopped()
    {
        var journal = NewProcess();
        using var stopping = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        using var recorder = new AgentExitRecorderService(journal, lifetime);

        await recorder.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(journal.LastExitPath));
        stopping.Cancel();

        var report = NewProcess().GetPendingReport();
        Assert.Equal(AgentExitKinds.Stopped, report!.Kind);
    }

    [Fact]
    public async Task Recorder_HostStopAfterARestart_KeepsTheRestartReason()
    {
        var journal = NewProcess();
        using var stopping = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        using var recorder = new AgentExitRecorderService(journal, lifetime);
        await recorder.StartAsync(TestContext.Current.CancellationToken);

        new AgentProcessRestarter(journal, _ => { }).Restart("self-repair: polling stalled");
        stopping.Cancel();

        var report = NewProcess().GetPendingReport();
        Assert.Equal(AgentExitKinds.Restart, report!.Kind);
        Assert.Equal("self-repair: polling stalled", report.Reason);
    }

    [Fact]
    public void DescribeCrash_NamesTheExceptionTypeAndMessage()
    {
        var text = AgentExitRecorderService.DescribeCrash(new InvalidOperationException("bad state"));

        Assert.Equal("Unhandled InvalidOperationException: bad state", text);
    }
}
