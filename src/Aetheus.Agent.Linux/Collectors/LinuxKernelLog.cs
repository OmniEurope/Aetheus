// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Aetheus.Agent.Linux.Collectors;

/// <summary>
/// The kernel log, read in this order: the kernel ring buffer through <c>/dev/kmsg</c>, read without
/// blocking (one record per <c>read(2)</c>, <c>EAGAIN</c> at the end), then the systemd journal
/// (<see cref="LinuxJournalKernelLog"/>). The shipped unit sets <c>ProtectKernelLogs=true</c> and the
/// kernel may restrict <c>/dev/kmsg</c> to <c>CAP_SYSLOG</c> (<c>kernel.dmesg_restrict=1</c>), so the
/// open fails there and the journal, readable through the <c>systemd-journal</c> group the installer
/// grants, answers. When neither can be read (an install from before that grant, no systemd) the result
/// is null, which the agent reports as "victims not readable". No privilege is requested at run time.
/// </summary>
internal static class LinuxKernelLog
{
    private const string KmsgPath = "/dev/kmsg";
    private const int ORdOnly = 0;
    private const int ONonBlock = 0x800; // O_NONBLOCK on x86_64 and aarch64
    private const int EAgain = 11;
    private const int EPipe = 32; // the record was overwritten while reading: the next one follows
    private const int RecordBufferSize = 8192;
    private const int MaxRecords = 100_000;

    internal static IReadOnlyList<string>? ReadRecords() =>
        OperatingSystem.IsLinux() ? ReadRecords(ReadKmsg, LinuxJournalKernelLog.Read) : null;

    /// <summary>
    /// The first source that can be read wins; an empty answer from <paramref name="readKmsg"/> is a
    /// readable buffer without records and ends the search, a null one falls through to the journal.
    /// </summary>
    internal static IReadOnlyList<string>? ReadRecords(
        Func<IReadOnlyList<string>?> readKmsg,
        Func<IReadOnlyList<string>?> readJournal)
    {
        ArgumentNullException.ThrowIfNull(readKmsg);
        ArgumentNullException.ThrowIfNull(readJournal);
        return readKmsg() ?? readJournal();
    }

    private static IReadOnlyList<string>? ReadKmsg()
    {
        var fd = open(KmsgPath, ORdOnly | ONonBlock);
        if (fd < 0) return null;
        try
        {
            var records = new List<string>();
            var buffer = new byte[RecordBufferSize];
            for (var i = 0; i < MaxRecords; i++)
            {
                var bytesRead = read(fd, buffer, buffer.Length);
                if (bytesRead > 0)
                {
                    var record = Encoding.UTF8.GetString(buffer, 0, (int)bytesRead);
                    var newline = record.IndexOf('\n', StringComparison.Ordinal);
                    records.Add(newline < 0 ? record : record[..newline]);
                    continue;
                }
                var error = bytesRead < 0 ? Marshal.GetLastPInvokeError() : 0;
                if (error == EPipe) continue;
                // A first read refused (EPERM, EINVAL...) means the log is not readable, not empty.
                if (error != 0 && error != EAgain && records.Count == 0) return null;
                break; // EAGAIN (end of the buffer), 0, or a later error: what was read is kept
            }
            return records;
        }
        finally
        {
            _ = close(fd);
        }
    }

    // CA5392 asks for DefaultDllImportSearchPaths, which selects among WINDOWS DLL search paths and
    // has no effect on the Unix loader; these calls only run on Linux (guarded above).
    [SuppressMessage("Security", "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
        Justification = "libc on Linux; the attribute only constrains the Windows DLL search order.")]
    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [SuppressMessage("Security", "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
        Justification = "libc on Linux; the attribute only constrains the Windows DLL search order.")]
    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, [Out] byte[] buffer, nint count);

    [SuppressMessage("Security", "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
        Justification = "libc on Linux; the attribute only constrains the Windows DLL search order.")]
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
