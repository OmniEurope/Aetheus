// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// ACORE-001 for operation tasks: stops a <c>sudo -n</c> command together with the root processes it started.
/// <para>
/// The agent cannot see those processes: its unit runs with <c>ProtectProc=invisible</c>, so /proc lists no
/// process of another user, and a snapshot of the tree finds neither sudo's command nor sudo itself. It cannot
/// kill them either: a signal from the agent's account to a root process fails with EPERM. Killing sudo with
/// SIGKILL (what a tree kill does) ended the wrapper alone and left its root child running under PID 1, while
/// the empty snapshot let the runner claim the tree had stopped.
/// </para>
/// <para>
/// So the command is started under <c>setsid</c>: sudo leads a process group of its own, which every process it
/// starts joins, invisible or not. The stop sends SIGTERM to sudo, which relays it to its command (sudo accepts
/// signals from the invoking user and forwards them) and exits only once that command has exited. The proof
/// does not read /proc: <c>kill(-group, 0)</c> answers ESRCH only when no process of the group remains, and
/// EPERM while a root member is still alive. Past the grace, the group gets SIGKILL (which reaches only the
/// agent's own members) and, without that proof, the stop is reported as not proven. No sudoers rule is
/// added: setsid runs as the agent, before sudo, and sudo still matches the same argv.
/// </para>
/// </summary>
internal static class SudoProcessGroup
{
    /// <summary>util-linux setsid, present on every supported distribution (Essential on Debian and Ubuntu).</summary>
    internal const string SetsidPath = "/usr/bin/setsid";

    internal const int SigKill = 9;
    internal const int SigTerm = 15;
    internal const int Eperm = 1;
    internal const int Esrch = 3;

    private static readonly TimeSpan DefaultTermGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultKillWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Rewrites a <c>sudo</c> start so it runs as <c>setsid -w sudo ...</c>. Returns false (and leaves the start
    /// untouched) for anything else, or when the arguments are a single string the rewrite could not prefix.
    /// <c>-w</c> keeps the exit code of the command should setsid ever have to fork.
    /// </summary>
    internal static bool Isolate(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!string.Equals(startInfo.FileName, "sudo", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(startInfo.Arguments))
            return false;

        startInfo.FileName = SetsidPath;
        startInfo.ArgumentList.Insert(0, "-w");
        startInfo.ArgumentList.Insert(1, "sudo");
        return true;
    }

    /// <summary>Stops the group led by <paramref name="process"/> (see the type summary). True only with the proof
    /// that sudo and every process of its group have exited.</summary>
    internal static Task<bool> StopAsync(Process process, ILogger logger) =>
        StopAsync(
            process.Id, () => ExecutorProcessRunner.HasExited(process), LinuxProcessGroupSignals.Instance, logger,
            DefaultTermGrace, DefaultKillWait);

    internal static async Task<bool> StopAsync(
        int processId,
        Func<bool> hasExited,
        IProcessGroupSignals signals,
        ILogger logger,
        TimeSpan termGrace,
        TimeSpan killWait)
    {
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(logger);

        if (hasExited() && IsGroupEmpty(signals, processId))
            return true;

        if (!hasExited() && signals.GetProcessGroup(processId) != processId)
        {
            // setsid did not give sudo a group of its own: the group signals below would reach the agent's own
            // group. Sudo is asked to stop on its own; its root children stay out of sight, so there is no proof.
            signals.Signal(processId, SigTerm);
            if (!await WaitAsync(hasExited, termGrace).ConfigureAwait(false))
                signals.Signal(processId, SigKill);
            logger.LogCritical(
                "Process {Pid} (sudo) was not running in its own process group; its privileged children could not be proven terminated",
                processId);
            return false;
        }

        // Sudo relays SIGTERM to its command and exits once the command has exited. A sudo already reaped is never
        // signalled by its id, which another process may hold by now; its group id stays reserved while it has members.
        if (!hasExited())
            signals.Signal(processId, SigTerm);
        else
            signals.Signal(-processId, SigTerm);
        if (await WaitAsync(() => hasExited() && IsGroupEmpty(signals, processId), termGrace).ConfigureAwait(false))
            return true;

        logger.LogWarning(
            "Process group {Pgid} (sudo) still had members {Grace}s after SIGTERM; sending SIGKILL to the group",
            processId, termGrace.TotalSeconds);
        signals.Signal(-processId, SigKill);
        if (await WaitAsync(() => hasExited() && IsGroupEmpty(signals, processId), killWait).ConfigureAwait(false))
            return true;

        logger.LogCritical(
            "Process group {Pgid} (sudo) could not be proven terminated: a privileged process of it may still be running after its task ended",
            processId);
        return false;
    }

    // kill(-pgid, 0): ESRCH means no process of the group remains; 0 or EPERM mean one is still there.
    private static bool IsGroupEmpty(IProcessGroupSignals signals, int processGroupId) =>
        signals.Signal(-processGroupId, 0) == Esrch;

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan budget)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (condition()) return true;
            if (watch.Elapsed >= budget) return false;
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }
}

/// <summary>The two system calls the group stop needs, replaceable so its decisions are asserted on any platform.</summary>
internal interface IProcessGroupSignals
{
    /// <summary><c>kill(pid, signal)</c>: 0 on success, else the errno (EPERM, ESRCH...).</summary>
    int Signal(int processId, int signal);

    /// <summary><c>getpgid(pid)</c>, or -1 when it fails.</summary>
    int GetProcessGroup(int processId);
}

internal sealed class LinuxProcessGroupSignals : IProcessGroupSignals
{
    internal static readonly LinuxProcessGroupSignals Instance = new();

    public int Signal(int processId, int signal) =>
        kill(processId, signal) == 0 ? 0 : Marshal.GetLastPInvokeError();

    public int GetProcessGroup(int processId) => getpgid(processId);

    // CA5392 asks for DefaultDllImportSearchPaths, which selects among WINDOWS DLL search paths and has no effect
    // on the Unix loader. Only reached on Linux (ProcessRunner isolates sudo there and nowhere else).
    [SuppressMessage("Security", "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
        Justification = "libc on Linux; the attribute only constrains the Windows DLL search order.")]
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [SuppressMessage("Security", "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
        Justification = "libc on Linux; the attribute only constrains the Windows DLL search order.")]
    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);
}
