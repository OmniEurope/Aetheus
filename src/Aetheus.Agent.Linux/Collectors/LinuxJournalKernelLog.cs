// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Linux.Collectors;

/// <summary>
/// The kernel messages of the current boot, read from the systemd journal with <c>journalctl --dmesg</c>.
/// This is how the agent names an OOM victim under the shipped unit: <c>ProtectKernelLogs=true</c> hides
/// <c>/dev/kmsg</c> and the kernel may restrict it to <c>CAP_SYSLOG</c> (<c>kernel.dmesg_restrict=1</c>
/// on Ubuntu), but journald copies every kernel record into the journal, and the installer puts the unit
/// in the <c>systemd-journal</c> group (<c>SupplementaryGroups=</c>), which may read the journal files and
/// nothing else: no capability, no write access to the kernel log.
/// </summary>
internal static class LinuxJournalKernelLog
{
    internal const string Executable = "journalctl";

    // Only the current boot (the oom_kill counter restarts with it), plain message text, newest 20000
    // lines: far more than the OOM lines ever need, bounded for a chatty kernel.
    internal static readonly string[] Arguments = ["--no-pager", "--dmesg", "--boot=0", "--output=cat", "--lines=20000"];

    private const int MaxRecords = 100_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The kernel messages, or null when the journal cannot be read (see <see cref="ParseOutput"/>).</summary>
    internal static IReadOnlyList<string>? Read() => Read(Executable, Arguments);

    internal static IReadOnlyList<string>? Read(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        // Starting a tool that is not installed (no systemd) throws; that is "not readable", not a fault.
        try
        {
            using var process = new Process { StartInfo = startInfo };
            var lines = new List<string>();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (lines)
                {
                    if (lines.Count < MaxRecords) lines.Add(e.Data);
                }
            };
            // stderr is drained and dropped so a full pipe can never stall the child.
            process.ErrorDataReceived += static (_, _) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            process.WaitForExit(); // flushes the asynchronous output handlers
            lock (lines)
            {
                return ParseOutput(process.ExitCode, lines);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The message lines of a <c>journalctl --dmesg</c> run, or null when they are not a trustworthy
    /// answer. A failed run is unreadable. So is an empty one: a booted kernel always logs, so zero lines
    /// mean the caller is not in <c>systemd-journal</c> (journalctl then lists only its own entries and
    /// exits 0), journald stores nothing, or the journal is gone; reporting that as "no OOM kill in the
    /// log" would claim a proof the agent does not have.
    /// </summary>
    internal static IReadOnlyList<string>? ParseOutput(int exitCode, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (exitCode != 0) return null;
        var records = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(MaxRecords).ToList();
        return records.Count == 0 ? null : records;
    }
}
