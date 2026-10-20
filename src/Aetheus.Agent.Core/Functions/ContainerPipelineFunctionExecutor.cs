// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Runs a <c>type: aetheus</c> step of a container-isolated stage INSIDE the step container, never on
/// the host: the isolation holds and the function sees the container's files. The agent's
/// <c>aetheus-fn</c> executables are bind-mounted read-only at <see cref="MountPath"/> and the step
/// script runs <c>aetheus-fn &lt;function&gt;</c> through the ordinary container path
/// (<see cref="IContainerExecutor"/>: hardening, workspace, toolchain, secrets, timeout).
///
/// The contract is the host's: the task is checked against the catalog first
/// (<c>invalid-request</c>); a function this agent does not implement, or an agent that ships no
/// <c>aetheus-fn</c>, fails as <c>function-not-implemented</c>; whatever the function reports comes
/// back through the <see cref="AetheusFnExitCodes"/>. Its <c>##aetheus[setvariable]</c> lines reach the
/// step log like those of any container step. A timeout reports TimedOut; an external cancellation
/// propagates.
/// </summary>
public sealed class ContainerPipelineFunctionExecutor
{
    /// <summary>Where the <c>aetheus-fn</c> directory is mounted inside the step container.</summary>
    public const string MountPath = "/opt/aetheus-fn";

    /// <summary>The workspace inside a step container (the bind mount of the run's source tree).</summary>
    public const string ContainerWorkspace = "/w";

    /// <summary>The executable per C library, relative to the functions directory.</summary>
    internal static readonly string[] Executables = ["linux-x64/aetheus-fn", "linux-musl-x64/aetheus-fn"];

    private readonly HashSet<string> _implemented;
    private readonly Func<string?, PipelineFunctionDescriptor?> _findFunction;
    private readonly IContainerExecutor _containerExecutor;
    private readonly string _functionsDirectory;

    public ContainerPipelineFunctionExecutor(
        IEnumerable<IPipelineFunction> functions,
        IContainerExecutor containerExecutor,
        IOptions<AetheusAgentOptions> options)
        : this(functions, containerExecutor, options?.Value.FunctionsDirectory!, PipelineFunctionCatalog.Find)
    {
    }

    /// <summary>Resolves functions through <paramref name="findFunction"/> instead of the shared
    /// catalog, so tests can describe contracts the catalog does not hold yet.</summary>
    internal ContainerPipelineFunctionExecutor(
        IEnumerable<IPipelineFunction> functions,
        IContainerExecutor containerExecutor,
        string functionsDirectory,
        Func<string?, PipelineFunctionDescriptor?> findFunction)
    {
        ArgumentNullException.ThrowIfNull(functions);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionsDirectory);
        _containerExecutor = containerExecutor ?? throw new ArgumentNullException(nameof(containerExecutor));
        _findFunction = findFunction ?? throw new ArgumentNullException(nameof(findFunction));
        _functionsDirectory = functionsDirectory;
        _implemented = functions.Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>True for the two function kinds, which this executor runs in the step container.</summary>
    public static bool Handles(OperationKind kind) =>
        kind is OperationKind.PipelineFunction or OperationKind.PipelineDeploymentFunction;

    public async Task<ExecutorResult> ExecuteAsync(
        ContainerSpec spec,
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(envVars);
        ArgumentNullException.ThrowIfNull(onOutput);

        var rejection = await CheckAsync(kind, target, envVars, onOutput).ConfigureAwait(false);
        if (rejection is not null)
            return rejection;

        var environment = new Dictionary<string, string>(envVars, StringComparer.Ordinal)
        {
            [PipelineFunctionRequest.WorkspaceVariable] = ContainerWorkspace
        };
        await onOutput($"Running Aetheus function '{target}' in the step container.", TaskLogLevel.Info)
            .ConfigureAwait(false);
        var result = await _containerExecutor.ExecuteAsync(
            spec,
            BuildCommand(target),
            environment,
            timeoutSeconds,
            onOutput,
            cancellationToken,
            [new ContainerReadOnlyMount(_functionsDirectory, MountPath)]).ConfigureAwait(false);
        return await MapResultAsync(target, result, onOutput).ConfigureAwait(false);
    }

    /// <summary>The host-side checks, made before any container starts. Null when the step may run.</summary>
    private async Task<ExecutorResult?> CheckAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (!Handles(kind))
            return new ExecutorResult(-1, false);
        if (!PipelineFunctionRequest.TryCreate(_findFunction, kind, target, envVars, out var request, out var reason))
            return await PipelineFunctionOperationExecutor.FailAsync(
                onOutput, PipelineFunctionOperationExecutor.InvalidRequest, reason!).ConfigureAwait(false);
        if (!_implemented.Contains(request!.Function.Name))
            return await PipelineFunctionOperationExecutor.FailAsync(
                onOutput,
                PipelineFunctionOperationExecutor.FunctionNotImplemented,
                $"Function '{request.Function.Name}' is in the catalog but this agent has no implementation for it; update the agent.")
                .ConfigureAwait(false);
        var missing = Executables.Where(path => !File.Exists(Path.Combine(_functionsDirectory, path))).ToList();
        if (missing.Count > 0)
            return await PipelineFunctionOperationExecutor.FailAsync(
                onOutput,
                PipelineFunctionOperationExecutor.FunctionNotImplemented,
                $"This agent does not ship aetheus-fn ({string.Join(", ", missing)} missing under {_functionsDirectory}), so it cannot run function '{request.Function.Name}' inside the step container; update the agent.")
                .ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The step script: picks the executable built for the container's C library (musl on Alpine,
    /// glibc elsewhere) and replaces the shell with it. The function name was checked against the
    /// catalog (a lowercase kebab-case token), and it is single-quoted all the same.
    /// </summary>
    internal static string BuildCommand(string function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        if (function.Contains('\'', StringComparison.Ordinal) || function.Contains('\n', StringComparison.Ordinal))
            throw new ArgumentException("A function name cannot be quoted for the step script.", nameof(function));
        return string.Join(
            '\n',
            $"if [ -e /lib/ld-musl-x86_64.so.1 ]; then fn={MountPath}/linux-musl-x64/aetheus-fn; else fn={MountPath}/linux-x64/aetheus-fn; fi",
            $"exec \"$fn\" '{function}'");
    }

    /// <summary>Turns the container result into the step result. A timeout or an infrastructure
    /// failure keeps its own report; an <c>aetheus-fn</c> exit code becomes its failure code; any other
    /// non-zero exit (the shell could not start it, the process died) is a <c>function-error</c>.</summary>
    internal static async Task<ExecutorResult> MapResultAsync(
        string function, ExecutorResult result, Func<string, TaskLogLevel, Task> onOutput)
    {
        if (result.TimedOut || result.FailureCode is not null || result.ExitCode == 0)
            return result;
        var code = AetheusFnExitCodes.ToFailureCode(result.ExitCode);
        var reason = code is not null
            ? $"Function '{function}' failed in the step container ({code}); see the step log."
            : result.ExitCode is 126 or 127
                ? $"aetheus-fn could not start in the step image (exit code {result.ExitCode}) while running function '{function}'; an Alpine (musl) image needs libstdc++ and libgcc, as the node Alpine images ship them."
                : $"aetheus-fn exited with code {result.ExitCode} in the step container while running function '{function}'.";
        return await PipelineFunctionOperationExecutor.FailAsync(
            onOutput, code ?? PipelineFunctionOperationExecutor.FunctionError, reason).ConfigureAwait(false);
    }
}
