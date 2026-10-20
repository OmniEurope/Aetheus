// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Executors;

internal static class ProcessTreeSnapshot
{
    private const uint Th32csSnapProcess = 0x00000002;

    internal static IReadOnlyList<Process> CaptureDescendants(int rootProcessId)
    {
        var parents = OperatingSystem.IsWindows()
            ? ReadWindowsParents()
            : OperatingSystem.IsLinux()
                ? ReadLinuxParents()
                : [];
        var descendants = new List<Process>();
        var known = new HashSet<int> { rootProcessId };
        var remaining = new Dictionary<int, int>(parents);

        while (remaining.Count > 0)
        {
            var discovered = remaining
                .Where(entry => known.Contains(entry.Value))
                .Select(entry => entry.Key)
                .ToArray();
            if (discovered.Length == 0)
                break;

            foreach (var processId in discovered)
            {
                remaining.Remove(processId);
                if (!known.Add(processId))
                    continue;
                try
                {
                    descendants.Add(Process.GetProcessById(processId));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // The descendant exited between the system snapshot and opening its handle.
                }
            }
        }

        return descendants;
    }

    private static Dictionary<int, int> ReadLinuxParents() => ReadParentsFromProc("/proc");

    // The parent of every process listed under a proc-style directory, so the parsing is asserted on a
    // temporary directory on any platform.
    internal static Dictionary<int, int> ReadParentsFromProc(string procRoot)
    {
        var result = new Dictionary<int, int>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(procRoot))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var processId))
                    continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                    var commandEnd = stat.LastIndexOf(')');
                    if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
                        continue;
                    var fields = stat[(commandEnd + 2)..].Split(
                        ' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length >= 2 && int.TryParse(fields[1], out var parentProcessId))
                        result[processId] = parentProcessId;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Processes can exit or become inaccessible while /proc is enumerated.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing or inaccessible /proc yields an empty best-effort snapshot.
        }

        return result;
    }

    private static Dictionary<int, int> ReadWindowsParents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == new IntPtr(-1))
            return result;

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
            };
            if (!Process32First(snapshot, ref entry))
                return result;

            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
