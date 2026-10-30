// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// F-32: typed Docker operations. Builds <c>docker</c> CLI argument lists statically - the only
/// user-controlled inputs are the container, image or Compose project identifier, validated by
/// <see cref="OperationTargetValidator"/>, and, for <see cref="OperationKind.DockerUpdateResources"/>, the two
/// numeric limits validated here. R-119: every change of the server's Docker page runs here, so the agent knows
/// which cached inventory it changed.
/// </summary>
public sealed partial class DockerOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<DockerOperationExecutor> logger) : IOperationExecutor
{
    internal const string CpusVariable = "AETHEUS_DOCKER_CPUS";
    internal const string MemoryMbVariable = "AETHEUS_DOCKER_MEMORY_MB";

    private readonly AetheusAgentOptions _options = options.Value;

    public bool CanHandle(OperationKind kind) => kind is
        OperationKind.DockerRestartContainer or
        OperationKind.DockerStartContainer or
        OperationKind.DockerStopContainer or
        OperationKind.DockerPullImage or
        OperationKind.DockerRemoveContainer or
        OperationKind.DockerRemoveImage or
        OperationKind.DockerUpdateResources or
        OperationKind.DockerComposeProjectUp or
        OperationKind.DockerComposeProjectDown or
        OperationKind.DockerComposeProjectRestart or
        OperationKind.DockerPrune;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
        => ExecuteAsync(kind, target, new Dictionary<string, string>(), timeoutSeconds, onOutput, cancellationToken);

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        if (!CanHandle(kind)) return new ExecutorResult(-1, false);

        var targetFailure = await OperationExecutorFailure.ValidateTargetAsync(
            kind, target, onOutput, () => logger.LogWarning("Rejected docker target with invalid format"),
            "Invalid docker target format").ConfigureAwait(false);
        if (targetFailure is not null) return targetFailure;

        var commands = kind switch
        {
            OperationKind.DockerPrune => BuildPruneArgs(target),
            OperationKind.DockerUpdateResources => BuildUpdateArgs(target, envVars) is { } update ? [update] : null,
            _ => BuildArgs(kind, target) is { } args ? [args] : null
        };
        if (commands is null)
        {
            logger.LogWarning("Rejected docker {Operation} with invalid parameters", kind);
            await onOutput("Invalid docker resource limits", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        return await RunInOrderAsync(commands, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);
    }

    // The commands share the task's timeout; the first one that fails or times out stops the rest, like `a && b`.
    private async Task<ExecutorResult> RunInOrderAsync(
        IReadOnlyList<string[]> commands, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var result = new ExecutorResult(0, false);
        foreach (var args in commands)
        {
            var remaining = timeoutSeconds - (int)Stopwatch.GetElapsedTime(started).TotalSeconds;
            if (remaining <= 0) return new ExecutorResult(-1, true);

            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            result = await ProcessRunner.RunAsync(psi, remaining, onOutput, logger, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.TimedOut) return result;
        }
        return result;
    }

    // Exact `docker` argv per kind (unit-testable without spawning). Only the validated target
    // is variable; the verb is fixed. Null for a kind with no single fixed argv.
    internal static string[]? BuildArgs(OperationKind kind, string target) => kind switch
    {
        OperationKind.DockerRestartContainer => ["restart", target],
        OperationKind.DockerStartContainer => ["start", target],
        OperationKind.DockerStopContainer => ["stop", target],
        OperationKind.DockerPullImage => ["pull", target],
        OperationKind.DockerRemoveContainer => ["rm", target],
        OperationKind.DockerRemoveImage => ["rmi", target],
        OperationKind.DockerComposeProjectUp => ["compose", "-p", target, "up", "-d"],
        OperationKind.DockerComposeProjectDown => ["compose", "-p", target, "down"],
        OperationKind.DockerComposeProjectRestart => ["compose", "-p", target, "restart"],
        _ => null
    };

    /// <summary><c>docker update</c> with the limits the task carries; null when neither is given or one is not a
    /// positive number in the expected form, so nothing but a fixed flag and a validated number reaches the argv.</summary>
    internal static string[]? BuildUpdateArgs(string container, IReadOnlyDictionary<string, string> envVars)
    {
        var args = new List<string> { "update" };
        if (envVars.TryGetValue(CpusVariable, out var cpus))
        {
            if (!CpusRegex().IsMatch(cpus)
                || decimal.Parse(cpus, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) <= 0)
                return null;
            args.Add($"--cpus={cpus}");
        }
        if (envVars.TryGetValue(MemoryMbVariable, out var memory))
        {
            if (!MemoryMbRegex().IsMatch(memory)) return null;
            args.Add($"--memory={memory}m");
        }
        if (args.Count == 1) return null;
        args.Add(container);
        return [.. args];
    }

    /// <summary>One fixed prune command per target the task lists (validated beforehand, in this order).</summary>
    internal static IReadOnlyList<string[]>? BuildPruneArgs(string targets)
    {
        var commands = new List<string[]>();
        foreach (var part in targets.Split(','))
        {
            string[]? args = part switch
            {
                "containers" => ["container", "prune", "-f"],
                "images" => ["image", "prune", "-af"],
                "volumes" => ["volume", "prune", "-f"],
                _ => null
            };
            if (args is null) return null;
            commands.Add(args);
        }
        return commands;
    }

    // Up to 999 CPUs with two decimals; memory in whole MiB, 1 to 9 999 999 (no leading zero).
    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{1,2})?\z")]
    private static partial Regex CpusRegex();

    [GeneratedRegex(@"^[1-9][0-9]{0,6}\z")]
    private static partial Regex MemoryMbRegex();
}
