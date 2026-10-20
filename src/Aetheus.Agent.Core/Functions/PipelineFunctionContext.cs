// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Everything a function sees of its step: the workspace, its decoded <c>with:</c> parameters, the run
/// variables the task carried, a log sink and the output channel.
///
/// Outputs are restricted to the names the catalog declares for the function, because a later step can
/// only reference those, and the control plane checks the reference against the catalog before the run
/// starts. They are held until the function succeeds: a failed function publishes nothing, so a later
/// step never reads half an answer.
/// </summary>
public sealed class PipelineFunctionContext
{
    private readonly Func<string, TaskLogLevel, Task> _onOutput;
    private readonly Dictionary<string, string> _outputs = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private volatile bool _closed;

    public PipelineFunctionContext(
        PipelineFunctionDescriptor function,
        string? workspace,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> runVariables,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        Function = function ?? throw new ArgumentNullException(nameof(function));
        Workspace = workspace;
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        RunVariables = runVariables ?? throw new ArgumentNullException(nameof(runVariables));
        _onOutput = onOutput ?? throw new ArgumentNullException(nameof(onOutput));
    }

    /// <summary>The catalog entry being run.</summary>
    public PipelineFunctionDescriptor Function { get; }

    /// <summary>The checked-out workspace (<c>WORKSPACE</c>). Always set for a build function; may be
    /// null for a deployment function, which acts on an environment rather than on sources.</summary>
    public string? Workspace { get; }

    /// <summary>The step's <c>with:</c> values, already checked against the catalog.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The environment variables the task carried, run variables included.</summary>
    public IReadOnlyDictionary<string, string> RunVariables { get; }

    /// <summary>The outputs set so far, published by the executor only once the function succeeded.</summary>
    public IReadOnlyDictionary<string, string> Outputs
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, string>(_outputs, StringComparer.Ordinal);
        }
    }

    /// <summary>Writes one line to the step log. Ignored once the step has ended (a function abandoned
    /// on timeout cannot keep writing into a finished step).</summary>
    public Task Log(string message, TaskLogLevel level = TaskLogLevel.Info)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _closed ? Task.CompletedTask : _onOutput(message, level);
    }

    /// <summary>Records an output. Refuses a name the catalog does not declare for this function and a
    /// value that would break the one-line variable protocol.</summary>
    public void SetOutput(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!Function.Outputs.Contains(name, StringComparer.Ordinal) && !IsNamedOutput(name))
            throw new InvalidOperationException(
                $"Function '{Function.Name}' does not declare the output '{name}' in the catalog.");
        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
            throw new InvalidOperationException($"The value of output '{name}' must fit on one line.");
        lock (_gate)
        {
            if (_closed)
                throw new InvalidOperationException("The step has already ended; outputs can no longer be set.");
            _outputs[name] = value;
        }
    }

    /// <summary>Whether <paramref name="name"/> is the output the step named through the function's
    /// <see cref="PipelineFunctionDescriptor.NamedOutputParameter"/>: exactly that parameter's value, in
    /// the run-variable shape.</summary>
    private bool IsNamedOutput(string name) =>
        Function.NamedOutputParameter is { } parameter
        && Parameters.GetValueOrDefault(parameter) is { } named
        && string.Equals(named, name, StringComparison.Ordinal)
        && PipelineRunVariableName.IsValid(name);

    /// <summary>Ends the context: later logs are dropped and later outputs refused.</summary>
    internal void Close()
    {
        lock (_gate)
            _closed = true;
    }
}
