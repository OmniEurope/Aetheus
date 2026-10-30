// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// R-119: starts, stops and restarts the TeamSpeak server. The former <c>systemctl ... teamspeak3</c> shell strings ran
/// without root and carried no operation kind, so the TeamSpeak page kept its old state. The agent finds the installed
/// unit (<see cref="TeamspeakUnit"/>) and runs <c>sudo -n /bin/systemctl &lt;verb&gt; &lt;unit&gt;</c>, an argv the
/// aetheus-teamspeak grant names word for word (noexec kept). Nothing in the task reaches the argv: the target is
/// the fixed <c>"-"</c>.
/// </summary>
public sealed class TeamspeakServiceOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<TeamspeakServiceOperationExecutor> logger) : IOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // Replaceable so the unit choice is asserted without systemd.
    internal Func<string, CancellationToken, Task<bool>>? UnitExistsAsync { get; init; }

    // The process runner, replaceable so the argv is asserted without sudo.
    internal Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, ILogger, CancellationToken, Task<ExecutorResult>>
        RunProcessAsync
    { get; init; } = static (startInfo, timeoutSeconds, onOutput, processLogger, ct) =>
            ProcessRunner.RunAsync(startInfo, timeoutSeconds, onOutput, processLogger, ct);

    public bool CanHandle(OperationKind kind) =>
        kind is OperationKind.TeamspeakStart or OperationKind.TeamspeakStop or OperationKind.TeamspeakRestart;

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        if (!IsLinux)
            return await FailAsync(onOutput, "TeamSpeak operations are only supported on Linux").ConfigureAwait(false);
        if (!OperationTargetValidator.IsValid(kind, target))
            return await FailAsync(onOutput, $"Invalid TeamSpeak service target '{target}'.").ConfigureAwait(false);

        var verb = kind switch
        {
            OperationKind.TeamspeakStart => "start",
            OperationKind.TeamspeakStop => "stop",
            OperationKind.TeamspeakRestart => "restart",
            _ => null
        };
        if (verb is null)
            return await FailAsync(onOutput, $"Unsupported TeamSpeak operation {kind}.").ConfigureAwait(false);

        var unit = await TeamspeakUnit.ResolveAsync(
            UnitExistsAsync ?? ((name, ct) => TeamspeakUnit.ExistsAsync(name, logger, ct)), cancellationToken).ConfigureAwait(false);
        if (unit is null)
            return await FailAsync(onOutput, TeamspeakUnit.NoUnitMessage).ConfigureAwait(false);

        var psi = SudoProcessStartInfo.Create();
        foreach (var argument in TeamspeakUnit.BuildSystemctlArgv(verb, unit)) psi.ArgumentList.Add(argument);
        await onOutput($"systemctl {verb} {unit}", TaskLogLevel.Info).ConfigureAwait(false);
        var timeout = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        return await RunProcessAsync(psi, timeout, onOutput, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExecutorResult> FailAsync(Func<string, TaskLogLevel, Task> onOutput, string message)
    {
        await onOutput(message, TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(-1, false);
    }
}
