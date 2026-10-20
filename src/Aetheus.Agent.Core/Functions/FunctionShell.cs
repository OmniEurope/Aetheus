// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The shell idioms the ported scripts rely on, with the same meaning: a variable read from the
/// step's environment, <c>command -v</c>, <c>[ -x ]</c> and <c>$(tool args)</c>. A child process sees
/// the step's environment (the run variables over the agent's own), as a script's tools did.
/// </summary>
internal static class FunctionShell
{
    private const string ExitLinePrefix = "[process] exit-code=";

    /// <summary>The variable as the script saw it: the task's value, else the agent's environment.
    /// An empty value reads as unset, like <c>${NAME:?}</c> and <c>${NAME:-default}</c> treat it.</summary>
    public static string? Variable(PipelineFunctionContext context, string name)
    {
        var value = context.RunVariables.GetValueOrDefault(name) ?? Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>A ported script's input: the step's <c>with:</c> parameter when set, else the variable
    /// the script read.</summary>
    public static string? Input(PipelineFunctionContext context, string parameter, string variable) =>
        context.Parameters.GetValueOrDefault(parameter) is { Length: > 0 } value ? value : Variable(context, variable);

    /// <summary>A command as the script's shell ran it: a bare name is looked up on the step's
    /// <c>PATH</c>, a path is used as given.</summary>
    public static string Tool(PipelineFunctionContext context, string command) =>
        command.Contains('/', StringComparison.Ordinal) || command.Contains('\\', StringComparison.Ordinal)
            ? command
            : FindOnPath(context, command) ?? command;

    /// <summary><c>command -v</c>: the first executable of that name along <c>PATH</c>, or null.</summary>
    public static string? FindOnPath(PipelineFunctionContext context, string command)
    {
        var path = Variable(context, "PATH");
        if (path is null)
            return null;
        string[] names = OperatingSystem.IsWindows() ? [command + ".exe", command + ".cmd", command] : [command];
        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(IsExecutable);
    }

    /// <summary><c>[ -x path ]</c>: a file that exists and that someone may execute.</summary>
    public static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExecute) != 0;
    }

    /// <summary>A start description for <paramref name="fileName"/> run in
    /// <paramref name="workingDirectory"/> with the step's environment.</summary>
    public static ProcessStartInfo StartInfo(
        PipelineFunctionContext context, string fileName, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in context.RunVariables)
            startInfo.Environment[name] = value;
        return startInfo;
    }

    /// <summary><c>$(tool args 2&gt;/dev/null)</c>: the standard output with its trailing newlines
    /// removed, or null when the tool could not run or failed.</summary>
    public static async Task<string?> CaptureAsync(
        IPipelineFunctionTools tools, ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var result = await tools.RunAsync(startInfo, (line, level) =>
        {
            if (level != TaskLogLevel.Warning && !line.StartsWith(ExitLinePrefix, StringComparison.Ordinal)
                && !line.StartsWith("[stderr] ", StringComparison.Ordinal))
                output.Append(line).Append('\n');
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut ? output.ToString().TrimEnd('\n') : null;
    }

    /// <summary>Whether the file holds exactly the bytes of that SHA-256 (lowercase hex), as
    /// <c>sha256sum -c</c> checks it.</summary>
    public static async Task<bool> HasSha256Async(string path, string expectedHex, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), expectedHex, StringComparison.Ordinal);
    }
}
