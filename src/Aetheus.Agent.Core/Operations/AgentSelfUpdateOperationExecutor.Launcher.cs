// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Operations;

internal static class AgentSelfUpdateLauncher
{
    internal static void LaunchDetached(
        string scriptPath, bool isWindows, ILogger logger, IReadOnlyList<string>? scriptArgs = null)
    {
        var process = isWindows
            ? BuildWindowsUpdaterProcess(scriptPath, scriptArgs)
            : BuildLinuxUpdaterProcess(scriptPath, scriptArgs);
        var child = Process.Start(process)
            ?? throw new InvalidOperationException("Failed to launch detached self-update helper");
        logger.LogInformation("Detached self-update helper started (pid {Pid})", child.Id);
    }

    private static ProcessStartInfo BuildWindowsUpdaterProcess(
        string scriptPath, IReadOnlyList<string>? scriptArgs)
    {
        var process = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
            process.ArgumentList.Add(argument);
        AddScriptArguments(process, scriptArgs);
        return process;
    }

    internal static ProcessStartInfo BuildLinuxUpdaterProcess(
        string scriptPath, IReadOnlyList<string>? scriptArgs)
    {
        var setsidPath = File.Exists("/usr/bin/setsid")
            ? "/usr/bin/setsid"
            : File.Exists("/bin/setsid") ? "/bin/setsid" : null;
        var process = new ProcessStartInfo
        {
            FileName = setsidPath ?? "sh",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (setsidPath is not null) process.ArgumentList.Add("sh");
        process.ArgumentList.Add(scriptPath);
        AddScriptArguments(process, scriptArgs);
        return process;
    }

    private static void AddScriptArguments(ProcessStartInfo process, IReadOnlyList<string>? arguments)
    {
        foreach (var argument in arguments ?? []) process.ArgumentList.Add(argument);
    }

    internal static async Task SafeOutputAsync(
        Func<string, TaskLogLevel, Task> onOutput, string message, TaskLogLevel level)
    {
        try { await onOutput(message, level).ConfigureAwait(false); }
        catch (Exception) { /* logging best-effort during teardown */ }
    }
}
