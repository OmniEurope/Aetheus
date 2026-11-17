// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Runs <c>type: aetheus</c> steps (<see cref="OperationKind.PipelineFunction"/> and
/// <see cref="OperationKind.PipelineDeploymentFunction"/>): checks the task against
/// <see cref="PipelineFunctionCatalog"/>, routes it to the registered <see cref="IPipelineFunction"/>
/// of that name, bounds it with the task timeout and publishes its outputs with
/// <c>##aetheus[setvariable]</c> only when it succeeded.
///
/// Failures are explicit: <c>invalid-request</c> (the task does not match the catalog),
/// <c>function-not-implemented</c> (a catalog entry this agent has no code for) and
/// <c>function-error</c> (the function failed or threw). A timeout reports TimedOut; an external
/// cancellation propagates. Neither ever reads as success.
/// </summary>
public sealed class PipelineFunctionOperationExecutor : EnvironmentOperationExecutor
{
    public const string InvalidRequest = "invalid-request";
    public const string FunctionNotImplemented = "function-not-implemented";
    public const string FunctionError = "function-error";

    // How long a cancelled or timed-out function is given to end before the step stops waiting for it.
    private static readonly TimeSpan DefaultStopGrace = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _stopGrace;
    private readonly Dictionary<string, IPipelineFunction> _functions;
    private readonly Func<string?, PipelineFunctionDescriptor?> _findFunction;

    public PipelineFunctionOperationExecutor(IEnumerable<IPipelineFunction> functions)
        : this(functions, PipelineFunctionCatalog.Find)
    {
    }

    /// <summary>Resolves functions through <paramref name="findFunction"/> instead of the shared
    /// catalog, so tests can describe contracts (a required parameter, a deployment function) the
    /// catalog does not hold yet.</summary>
    internal PipelineFunctionOperationExecutor(
        IEnumerable<IPipelineFunction> functions, Func<string?, PipelineFunctionDescriptor?> findFunction,
        TimeSpan? stopGrace = null)
    {
        ArgumentNullException.ThrowIfNull(functions);
        _stopGrace = stopGrace ?? DefaultStopGrace;
        _findFunction = findFunction ?? throw new ArgumentNullException(nameof(findFunction));
        _functions = new Dictionary<string, IPipelineFunction>(StringComparer.Ordinal);
        foreach (var function in functions)
            if (!_functions.TryAdd(function.Name, function))
                throw new InvalidOperationException($"Two pipeline functions are registered as '{function.Name}'.");
    }

    public override bool CanHandle(OperationKind kind) =>
        kind is OperationKind.PipelineFunction or OperationKind.PipelineDeploymentFunction;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envVars);
        ArgumentNullException.ThrowIfNull(onOutput);

        if (!CanHandle(kind)) return new ExecutorResult(-1, false);

        if (!PipelineFunctionRequest.TryCreate(_findFunction, kind, target, envVars, out var request, out var rejection))
            return await FailAsync(onOutput, InvalidRequest, rejection!).ConfigureAwait(false);

        if (!_functions.TryGetValue(request!.Function.Name, out var function))
            return await FailAsync(
                onOutput,
                FunctionNotImplemented,
                $"Function '{request.Function.Name}' is in the catalog but this agent has no implementation for it; update the agent.")
                .ConfigureAwait(false);

        var context = new PipelineFunctionContext(
            request.Function, request.Workspace, request.Parameters, envVars, onOutput);
        await onOutput($"Running Aetheus function '{request.Function.Name}'.", TaskLogLevel.Info).ConfigureAwait(false);
        var failure = await RunAsync(function, context, timeoutSeconds, onOutput, cancellationToken).ConfigureAwait(false);
        return failure ?? await PublishAsync(context, onOutput).ConfigureAwait(false);
    }

    /// <summary>Runs the function under the timeout. Returns null on success, the failed result
    /// otherwise; an external cancellation is rethrown.</summary>
    private async Task<ExecutorResult?> RunAsync(
        IPipelineFunction function, PipelineFunctionContext context, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        PipelineFunctionResult result;
        Task<PipelineFunctionResult>? call = null;
        try
        {
            // WaitAsync lets the step answer a cancellation without waiting for a function that ignores
            // its token, but the answer is never "stopped" while the call still runs: after a cancellation
            // the call is given a grace to end, and what has not ended by then is reported as such (below).
            call = function.ExecuteAsync(context, linked.Token);
            result = await call.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var ended = await EndedWithinGraceAsync(call).ConfigureAwait(false);
            context.Close();
            if (!ended)
                await onOutput(
                    $"Function '{function.Name}' did not end within {_stopGrace.TotalSeconds:0.##}s of its cancellation; it may still be running.",
                    TaskLogLevel.Error).ConfigureAwait(false);
            await onOutput(
                $"Function '{function.Name}' exceeded its timeout of {timeoutSeconds}s; no output was published.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The external cancellation is acknowledged as a stop only once the function really ended
            // (its mutations ended or were cancelled); otherwise the agent must not report the task stopped.
            if (!await EndedWithinGraceAsync(call).ConfigureAwait(false))
                throw new PipelineFunctionNotStoppedException(function.Name, cancellationToken);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            context.Close();
            return await FailAsync(onOutput, FunctionError, $"Function '{function.Name}' failed: {ex.Message}")
                .ConfigureAwait(false);
        }
        finally
        {
            // Also on the external cancellation that propagates: nothing the abandoned call writes later
            // may reach the step.
            context.Close();
        }

        return result.Succeeded
            ? null
            : await FailAsync(onOutput, FunctionError, $"Function '{function.Name}' failed: {result.FailureReason}")
                .ConfigureAwait(false);
    }

    /// <summary>Whether the function call has ended (in any way) within the stop grace. A null call
    /// never started, so there is nothing left running. A call that outlives the grace has its later
    /// failure observed, so it never surfaces as an unobserved task exception.</summary>
    private async Task<bool> EndedWithinGraceAsync(Task? call)
    {
        if (call is null || call.IsCompleted)
            return true;

        using var grace = new CancellationTokenSource();
        var winner = await Task.WhenAny(call, Task.Delay(_stopGrace, grace.Token)).ConfigureAwait(false);
        await grace.CancelAsync().ConfigureAwait(false);
        if (winner == call)
            return true;

        _ = call.ContinueWith(static task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return false;
    }

    /// <summary>Publishes the outputs of a successful function. Every declared output must be set: a
    /// later step may reference any of them, and a missing one would silently read as empty.</summary>
    private static async Task<ExecutorResult> PublishAsync(
        PipelineFunctionContext context, Func<string, TaskLogLevel, Task> onOutput)
    {
        var outputs = context.Outputs;
        var missing = context.Function.Outputs.Where(name => !outputs.ContainsKey(name)).ToList();
        if (missing.Count > 0)
            return await FailAsync(
                onOutput,
                FunctionError,
                $"Function '{context.Function.Name}' succeeded without setting its declared output(s) {string.Join(", ", missing)}.")
                .ConfigureAwait(false);

        foreach (var name in context.Function.Outputs)
            await onOutput($"##aetheus[setvariable name={name}]{outputs[name]}", TaskLogLevel.Info).ConfigureAwait(false);
        // The optional output the step named (NamedOutputParameter); the context refused any other name.
        foreach (var (name, value) in outputs.Where(output => !context.Function.Outputs.Contains(output.Key, StringComparer.Ordinal)))
            await onOutput($"##aetheus[setvariable name={name}]{value}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"Function '{context.Function.Name}' succeeded.", TaskLogLevel.Info).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    internal static async Task<ExecutorResult> FailAsync(
        Func<string, TaskLogLevel, Task> onOutput, string code, string reason)
    {
        await onOutput(reason, TaskLogLevel.Error).ConfigureAwait(false);
        return new ExecutorResult(1, false, code, reason);
    }
}
