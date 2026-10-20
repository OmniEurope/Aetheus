// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Linux.Collectors;

/// <summary>
/// Inode counts of a mounted filesystem through libc <c>statvfs(3)</c>, the call <c>df -i</c> makes,
/// without starting a process on every heartbeat.
/// </summary>
internal static class LinuxStatvfs
{
    // struct statvfs on 64-bit glibc and musl (x86_64, aarch64): f_bsize, f_frsize, f_blocks, f_bfree,
    // f_bavail, f_files, f_ffree, ... each 8 bytes. f_files and f_ffree are the 6th and 7th fields.
    // The buffer is larger than the struct (112 bytes on glibc) so a libc with a longer tail cannot
    // write past it.
    private const int BufferSize = 256;
    private const int FilesOffset = 5 * sizeof(ulong);
    private const int FreeFilesOffset = 6 * sizeof(ulong);

    /// <summary>Null when the call fails or the process is not 64-bit (the layout above would not hold).</summary>
    internal static InodeUsage? Read(string mountPoint)
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess) return null;

        var buffer = new byte[BufferSize];
        if (statvfs(mountPoint, buffer) != 0) return null;

        return InodeUsage.FromStatvfs(
            BitConverter.ToUInt64(buffer, FilesOffset),
            BitConverter.ToUInt64(buffer, FreeFilesOffset));
    }

    // CA5392 asks for DefaultDllImportSearchPaths, which selects among WINDOWS DLL search paths and
    // has no effect on the Unix loader; this call only runs on Linux (guarded above).
    [SuppressMessage("Security", "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
        Justification = "libc on Linux; the attribute only constrains the Windows DLL search order.")]
    [DllImport("libc", SetLastError = true)]
    private static extern int statvfs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
}
