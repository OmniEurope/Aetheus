// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Executors;

public sealed partial class DockerExecutor(
    ICommandValidator commandValidator,
    IOptions<AetheusAgentOptions> options,
    ExecutorProcessRunner processRunner,
    ILogger<DockerExecutor> logger) : IExecutor
{
    private static readonly Regex ContainerIdPattern = ContainerIdRegex();
    private readonly AetheusAgentOptions _options = options.Value;

    public ExecutorType Type => ExecutorType.Docker;

    // The docker client, replaceable so the exec path runs against a stand-in program in tests.
    internal string DockerProgram { get; init; } = "docker";

    public async Task<ExecutorResult> ExecuteAsync(
        string command,
        Dictionary<string, string> environmentVariables,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (!TryParseExecCommand(command, out var containerId, out var innerCommand, out var parseError))
        {
            // A non-empty containerId means the split succeeded but the id failed the format check
            // (vs. a missing space, which yields an empty id) - preserve the original per-case logging.
            if (containerId.Length > 0)
            {
                logger.LogWarning("Invalid container ID format blocked");
            }
            await onOutput(parseError, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        if (!commandValidator.IsAllowed(innerCommand))
        {
            var reason = commandValidator.GetRejectionReason(innerCommand) ?? "Unknown";
            logger.LogWarning("Docker command blocked by validator: {Reason}", reason);
            await onOutput($"Command blocked by security policy - {reason}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var dangerousEnvironmentVariables =
            commandValidator.GetDangerousEnvironmentVariableNames(environmentVariables);
        if (dangerousEnvironmentVariables.Count > 0)
        {
            var names = string.Join(", ", dangerousEnvironmentVariables);
            logger.LogWarning("Dangerous environment variables detected: {Names}", names);
            await onOutput(
                $"Blocked dangerous environment variable(s): {names}. Rename application settings to a scoped name such as DEPLOY_ENV.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var invalidEnvironmentVariables = environmentVariables.Keys
            .Where(static key => !IsValidShellKey(key))
            .ToArray();
        if (invalidEnvironmentVariables.Length > 0)
        {
            var names = string.Join(", ", invalidEnvironmentVariables);
            logger.LogWarning("Invalid environment variable names detected: {Names}", names);
            await onOutput(
                $"Blocked invalid environment variable name(s): {names}.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        var psi = BuildDockerExec(containerId, DockerProgram);
        var standardInputScript = BuildStandardInputScript(environmentVariables, innerCommand);

        logger.LogDebug("Docker exec: container={Container} (timeout={Timeout}s)", containerId, timeoutSeconds);
        return await processRunner.RunAsync(
            psi,
            timeoutSeconds,
            onOutput,
            cancellationToken,
            async (standardInput, inputCancellationToken) =>
                await standardInput.WriteAsync(standardInputScript.AsMemory(), inputCancellationToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
    }

    internal static ProcessStartInfo BuildDockerExec(string containerId, string dockerProgram = "docker")
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dockerProgram,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(containerId);
        startInfo.ArgumentList.Add("/bin/sh");
        return startInfo;
    }

    internal static string BuildStandardInputScript(
        IReadOnlyDictionary<string, string> environmentVariables,
        string innerCommand)
    {
        var script = new StringBuilder();
        foreach (var (key, value) in environmentVariables)
            script.Append("export ").Append(key).Append('=').Append(ShellQuote(value)).Append('\n');
        script.Append(innerCommand).Append('\n');
        return script.ToString();
    }

    internal static bool IsValidShellKey(string key) =>
        key.Length > 0
        && (char.IsAsciiLetter(key[0]) || key[0] == '_')
        && key.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    /// <summary>
    /// Pure parse of a docker-exec task payload ("&lt;containerId&gt; &lt;inner command&gt;") into its two
    /// parts, validating the container id against <see cref="ContainerIdPattern"/>. Returns false with a
    /// user-facing <paramref name="error"/> when the payload has no space (missing id/command) or the id
    /// is malformed. Extracted so the parsing/validation is unit-testable without spawning docker.
    /// </summary>
    internal static bool TryParseExecCommand(string command, out string containerId, out string innerCommand, out string error)
    {
        containerId = string.Empty;
        innerCommand = string.Empty;
        error = string.Empty;

        var spaceIndex = command.IndexOf(' ');
        if (spaceIndex < 0)
        {
            error = "Invalid docker exec command: missing container ID or command";
            return false;
        }

        containerId = command[..spaceIndex];
        innerCommand = command[(spaceIndex + 1)..];

        if (!ContainerIdPattern.IsMatch(containerId))
        {
            error = "Invalid container ID format";
            return false;
        }

        return true;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.\-]*$")]
    private static partial Regex ContainerIdRegex();
}
