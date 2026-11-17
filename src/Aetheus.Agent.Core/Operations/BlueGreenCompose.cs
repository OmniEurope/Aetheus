// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// How a blue-green step talks to Compose, and the only door it may use.
///
/// Every invocation carries the step's declared Compose inputs, so a path that bypassed this would
/// run the same project with different image tags - exactly the drift the typed steps replaced. The
/// agent environment is still inherited: Compose needs DOCKER_HOST, PATH and the rest of the service
/// runtime.
///
/// This is a collaborator rather than a set of private helpers because the executor's own file is
/// about decisions - migrate, start, switch, commit, undo - while these three are about the shape of
/// a `docker compose` invocation, and they are shared by all of them.
/// </summary>
internal sealed class BlueGreenCompose(IShellRunner shell)
{
    /// <summary>Caps captured process output so a runaway Compose log cannot exhaust agent memory.</summary>
    private const int MaxCapturedOutputBytes = 1024 * 1024;

    internal async Task<ShellExecResult> RunAsync(
        BlueGreenContext context, IReadOnlyList<string> extra, int timeoutSeconds, CancellationToken ct)
    {
        var args = context.ComposeArgs();
        args.AddRange(extra);
        return await shell.RunExecAsync(
            "docker", [.. args], context.ComposeEnvironment, string.Empty,
            inheritEnvironment: true, MaxCapturedOutputBytes, ct, TimeSpan.FromSeconds(timeoutSeconds))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Same invocation as <see cref="RunAsync"/>, but every output line reaches <paramref name="onLine"/>
    /// (second argument: true for stderr) as soon as Compose writes it. For steps that can run for
    /// minutes and must not leave the run log silent meanwhile.
    /// </summary>
    internal async Task<ShellExecResult> RunStreamingAsync(
        BlueGreenContext context, IReadOnlyList<string> extra, int timeoutSeconds,
        Func<string, bool, Task> onLine, CancellationToken ct)
    {
        var args = context.ComposeArgs();
        args.AddRange(extra);
        return await shell.RunExecStreamingAsync(
            "docker", [.. args], context.ComposeEnvironment, string.Empty,
            inheritEnvironment: true, MaxCapturedOutputBytes, onLine, ct, TimeSpan.FromSeconds(timeoutSeconds))
            .ConfigureAwait(false);
    }

    internal async Task<int> StopColourAsync(
        BlueGreenContext context, string colour, int timeoutSeconds, CancellationToken ct)
    {
        // Name the services explicitly: `--profile <colour> stop` with no arguments also stops
        // unprofiled services, which includes the one shared database.
        var result = await RunAsync(
            context, ["--profile", colour, "stop", $"back-{colour}", $"front-{colour}"], timeoutSeconds, ct)
            .ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <summary>
    /// Removes the idle colour's containers before it is started again.
    /// </summary>
    /// <remarks>
    /// `stop` leaves the containers in place, and a container that died or never got past `Created`
    /// keeps its published ports reserved, so the next deployment failed with "port is already
    /// allocated" and every following one failed the same way until someone intervened by hand.
    /// Only ever the idle colour, which carries no traffic and is rebuilt by the `up` that follows;
    /// the services are named explicitly so the shared database is never in scope, and no volume is
    /// touched - the containers are disposable, the data is not.
    /// </remarks>
    internal async Task<int> ResetIdleColourAsync(
        BlueGreenContext context, string colour, int timeoutSeconds, CancellationToken ct)
    {
        var result = await RunAsync(
            context, ["--profile", colour, "rm", "--force", "--stop", $"back-{colour}", $"front-{colour}"],
            timeoutSeconds, ct).ConfigureAwait(false);
        return result.ExitCode;
    }

    internal async Task DumpColourLogsAsync(
        BlueGreenContext context, string colour, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var logs = await RunAsync(
            context,
            ["--profile", colour, "logs", "--no-color", "--tail=200", $"back-{colour}", $"front-{colour}", "database"],
            timeoutSeconds, ct).ConfigureAwait(false);
        // Line by line: 200 lines across three services sent as one message exceeds the backend's
        // per-message limit, which rejects the whole batch and loses these diagnostics precisely when
        // a colour failed to start and they are the only evidence.
        if (logs.StdOut.Length == 0) return;
        foreach (var line in logs.StdOut.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            await onOutput(
                trimmed.Length <= SmokeOperationExecutor.MaxLogLineLength
                    ? trimmed
                    : trimmed[..SmokeOperationExecutor.MaxLogLineLength] + " …[truncated]",
                TaskLogLevel.Error).ConfigureAwait(false);
        }
    }
}
