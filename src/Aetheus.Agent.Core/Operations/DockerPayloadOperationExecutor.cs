// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// BACK-012: the Docker operations that carry a user-supplied payload (a Compose file, a script for
/// <c>docker exec</c>, a Dockerfile). The backend used to send them as <c>printf | base64 -d | ...</c>
/// shell strings, which the command validator refuses on sight, so they never ran. Here the payload
/// arrives base64-encoded in an environment variable and reaches <c>docker</c> as a file or as stdin;
/// the command itself is an argv built from the validated target only.
/// </summary>
public sealed class DockerPayloadOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<DockerPayloadOperationExecutor> logger) : IOperationExecutor
{
    internal const string ComposeVariable = "AETHEUS_DOCKER_COMPOSE_B64";
    internal const string ExecScriptVariable = "AETHEUS_DOCKER_EXEC_B64";
    internal const string DockerfileVariable = "AETHEUS_DOCKER_DOCKERFILE_B64";

    private readonly AetheusAgentOptions _options = options.Value;

    public bool CanHandle(OperationKind kind) => kind is
        OperationKind.DockerComposeUp or
        OperationKind.DockerExecScript or
        OperationKind.DockerBuildImage;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
        => ExecuteAsync(kind, target, new Dictionary<string, string>(), timeoutSeconds, onOutput, cancellationToken);

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        var targetFailure = await OperationExecutorFailure.ValidateTargetAsync(
            kind, target, onOutput, () => logger.LogWarning("Rejected docker payload target with invalid format"),
            "Invalid docker target format").ConfigureAwait(false);
        if (targetFailure is not null) return targetFailure;

        var variable = PayloadVariable(kind);
        if (!envVars.TryGetValue(variable, out var encoded) || string.IsNullOrEmpty(encoded))
        {
            await onOutput($"Missing {variable} env var.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        var payload = Decode(encoded);
        if (payload is null)
        {
            await onOutput($"{variable} is not valid base64.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        return kind switch
        {
            OperationKind.DockerExecScript => await RunAsync(
                BuildArgs(kind, target, string.Empty), timeoutSeconds, onOutput, cancellationToken, payload).ConfigureAwait(false),
            OperationKind.DockerComposeUp => await ComposeUpAsync(
                target, payload, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false),
            _ => await BuildImageAsync(target, payload, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<ExecutorResult> ComposeUpAsync(
        string stack, string payload, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        // The stack keeps its file in the agent's own work directory: relative paths in it (bind
        // mounts, env files) resolve there, and later compose commands find the same file again.
        var directory = Path.Combine(_options.WorkDirectory, "docker-compose", stack);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "compose.yml"), payload, cancellationToken).ConfigureAwait(false);
        return await RunAsync(BuildArgs(OperationKind.DockerComposeUp, stack, directory), timeoutSeconds, onOutput, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ExecutorResult> BuildImageAsync(
        string tag, string payload, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        // A fresh private directory per build, which is the whole build context: the Dockerfile cannot
        // COPY files of the agent or of anything else on the host.
        var directory = Directory.CreateTempSubdirectory("aetheus-docker-build-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "Dockerfile"), payload, cancellationToken).ConfigureAwait(false);
            return await RunAsync(BuildArgs(OperationKind.DockerBuildImage, tag, directory), timeoutSeconds, onOutput, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<ExecutorResult> RunAsync(
        IReadOnlyList<string> args, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken, string? standardInput = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return await ProcessRunner.RunAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken, standardInput)
            .ConfigureAwait(false);
    }

    // Exact `docker` argv per kind (unit-testable without spawning): only the validated target and
    // the agent's own temp directory vary.
    internal static string[] BuildArgs(OperationKind kind, string target, string directory) => kind switch
    {
        OperationKind.DockerComposeUp =>
            ["compose", "-f", Path.Combine(directory, "compose.yml"), "-p", target, "up", "-d"],
        OperationKind.DockerExecScript => ["exec", "-i", target, "sh"],
        OperationKind.DockerBuildImage =>
            ["build", "-t", target, "-f", Path.Combine(directory, "Dockerfile"), directory],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a docker payload operation.")
    };

    internal static string PayloadVariable(OperationKind kind) => kind switch
    {
        OperationKind.DockerComposeUp => ComposeVariable,
        OperationKind.DockerExecScript => ExecScriptVariable,
        OperationKind.DockerBuildImage => DockerfileVariable,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a docker payload operation.")
    };

    private static string? Decode(string encoded)
    {
        var buffer = new byte[encoded.Length];
        return Convert.TryFromBase64String(encoded, buffer, out var written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : null;
    }
}
