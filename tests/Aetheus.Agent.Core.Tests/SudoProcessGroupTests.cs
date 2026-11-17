// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// ACORE-001 for operation tasks: the stop of a <c>sudo -n</c> command. The agent can neither see (ProtectProc)
/// nor kill (EPERM) the root child of sudo, so the proof is the group signal, asserted here with a fake of the
/// two system calls.
/// </summary>
public sealed class SudoProcessGroupTests
{
    private const int Pid = 4242;
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void Isolate_SudoStart_RunsItUnderSetsidWithTheSameSudoArgv()
    {
        var start = new ProcessStartInfo { FileName = "sudo" };
        foreach (var argument in new[] { "-n", "/usr/bin/apt-get", "install", "-y", "ufw" })
            start.ArgumentList.Add(argument);

        Assert.True(SudoProcessGroup.Isolate(start));

        Assert.Equal(SudoProcessGroup.SetsidPath, start.FileName);
        Assert.Equal(["-w", "sudo", "-n", "/usr/bin/apt-get", "install", "-y", "ufw"], start.ArgumentList);
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("/usr/bin/sudo")]
    public void Isolate_OtherCommand_IsLeftUntouched(string fileName)
    {
        var start = new ProcessStartInfo { FileName = fileName };
        start.ArgumentList.Add("ps");

        Assert.False(SudoProcessGroup.Isolate(start));

        Assert.Equal(fileName, start.FileName);
        Assert.Equal(["ps"], start.ArgumentList);
    }

    // The defect seen on the test host: SIGKILL ended sudo, its root apt-get kept running under PID 1. The group
    // still answers EPERM for that invisible member, so the stop is not proven.
    [Fact]
    public async Task StopAsync_RootMemberOutlivesSudo_ReportsNoProofAfterKillingTheGroup()
    {
        var signals = new FakeSignals { RootMemberAlive = true };

        var proven = await StopAsync(signals);

        Assert.False(proven);
        Assert.Contains((Pid, SudoProcessGroup.SigTerm), signals.Sent);
        Assert.Contains((-Pid, SudoProcessGroup.SigKill), signals.Sent);
    }

    // Sudo relays SIGTERM to its command and exits after it: the empty group is the proof, no SIGKILL is sent.
    [Fact]
    public async Task StopAsync_GroupEmptiesAfterSigterm_ReportsProofWithoutSigkill()
    {
        var signals = new FakeSignals();
        signals.OnTerm = () => signals.SudoExited = true;

        var proven = await StopAsync(signals);

        Assert.True(proven);
        Assert.Equal((Pid, SudoProcessGroup.SigTerm), signals.Sent.First(sent => sent.Signal != 0));
        Assert.DoesNotContain(signals.Sent, sent => sent.Signal == SudoProcessGroup.SigKill);
    }

    // Sudo itself exiting is not enough: a root process left in the group keeps the stop unproven.
    [Fact]
    public async Task StopAsync_SudoExitedButGroupNotEmpty_ReportsNoProof()
    {
        var signals = new FakeSignals { RootMemberAlive = true };
        signals.OnTerm = () => signals.SudoExited = true;

        Assert.False(await StopAsync(signals));
    }

    // Without a group of its own, a group signal would reach the agent's group: only sudo is signalled, and no
    // proof is claimed.
    [Fact]
    public async Task StopAsync_SudoNotLeadingItsOwnGroup_NeverSignalsAGroupAndReportsNoProof()
    {
        var signals = new FakeSignals { ProcessGroup = 1000 };
        signals.OnTerm = () => signals.SudoExited = true;

        var proven = await StopAsync(signals);

        Assert.False(proven);
        Assert.DoesNotContain(signals.Sent, sent => sent.ProcessId < 0 && sent.Signal != 0);
    }

    // A reaped sudo's id may belong to another process by now: only its group, still reserved, is signalled.
    [Fact]
    public async Task StopAsync_SudoAlreadyReapedWithAMemberLeft_SignalsOnlyTheGroup()
    {
        var signals = new FakeSignals { SudoExited = true, RootMemberAlive = true };

        Assert.False(await StopAsync(signals));
        Assert.DoesNotContain(signals.Sent, sent => sent.ProcessId == Pid);
        Assert.Contains((-Pid, SudoProcessGroup.SigTerm), signals.Sent);
    }

    [Fact]
    public async Task StopAsync_AlreadyExitedWithEmptyGroup_SendsNoSignal()
    {
        var signals = new FakeSignals { SudoExited = true };

        Assert.True(await StopAsync(signals));
        Assert.DoesNotContain(signals.Sent, sent => sent.Signal != 0);
    }

    private static Task<bool> StopAsync(FakeSignals signals) =>
        SudoProcessGroup.StopAsync(Pid, () => signals.SudoExited, signals, NullLogger.Instance, Short, Short);

    private sealed class FakeSignals : IProcessGroupSignals
    {
        public bool SudoExited { get; set; }
        public bool RootMemberAlive { get; init; }
        public int ProcessGroup { get; init; } = Pid;
        public Action? OnTerm { get; set; }
        public List<(int ProcessId, int Signal)> Sent { get; } = [];

        public int Signal(int processId, int signal)
        {
            Sent.Add((processId, signal));
            if (processId == Pid && signal == SudoProcessGroup.SigTerm) OnTerm?.Invoke();
            if (processId == -Pid && signal == SudoProcessGroup.SigKill) SudoExited = true;
            if (processId == -Pid && signal == 0)
                return RootMemberAlive ? SudoProcessGroup.Eperm : SudoExited ? SudoProcessGroup.Esrch : 0;
            return 0;
        }

        public int GetProcessGroup(int processId) => ProcessGroup;
    }
}
