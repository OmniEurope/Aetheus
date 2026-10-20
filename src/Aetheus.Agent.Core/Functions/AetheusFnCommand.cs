// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The body of the <c>aetheus-fn</c> executable: <c>aetheus-fn &lt;function&gt;</c> runs one pipeline
/// function inside a step container, under the same contract as on the host. The task environment
/// carries <c>AETHEUS_FUNCTION_PARAMETERS_JSON</c> and <c>WORKSPACE</c> (the container workspace,
/// <c>/w</c>); the function runs through <see cref="PipelineFunctionOperationExecutor"/>, so the
/// catalog checks, the output rules and the <c>##aetheus[setvariable]</c> lines are the host's own.
/// Log lines go to stdout, errors to stderr, and the exit code names the failure
/// (<see cref="AetheusFnExitCodes"/>).
///
/// The step timeout belongs to the agent, which removes the container when it expires; the bound
/// here only keeps the executor's own timer within range.
/// </summary>
public static class AetheusFnCommand
{
    internal const int InContainerTimeoutSeconds = 7 * 24 * 3600;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (arguments.Count != 1 || string.IsNullOrWhiteSpace(arguments[0]))
        {
            await standardError.WriteLineAsync("Usage: aetheus-fn <function>").ConfigureAwait(false);
            return AetheusFnExitCodes.InvalidRequest;
        }

        var name = arguments[0];
        // An unknown name is still handed to the executor, which refuses it as invalid-request with
        // the same message as on the host.
        var kind = PipelineFunctionCatalog.Find(name)?.Operation ?? OperationKind.PipelineFunction;
        var executor = new PipelineFunctionOperationExecutor(PipelineFunctions.CreateAll());
        var result = await executor.ExecuteAsync(
            kind,
            name,
            environment,
            InContainerTimeoutSeconds,
            (line, level) => level == TaskLogLevel.Error
                ? standardError.WriteLineAsync(line)
                : standardOutput.WriteLineAsync(line),
            cancellationToken).ConfigureAwait(false);
        await standardOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
        await standardError.FlushAsync(cancellationToken).ConfigureAwait(false);
        return AetheusFnExitCodes.FromResult(result);
    }
}
