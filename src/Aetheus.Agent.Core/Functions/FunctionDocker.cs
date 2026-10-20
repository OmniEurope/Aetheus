// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The Docker CLI as the ported scripts ran it: <c>docker</c> looked up on the step's <c>PATH</c>, run
/// with the step's environment in the workspace (the agent's working directory for a deployment
/// function without one). <see cref="LinesAsync"/> is <c>$(docker ...)</c>, <see cref="RunAsync"/> a
/// command whose output goes to the step log, <see cref="QuietAsync"/> the same with
/// <c>&gt;/dev/null</c> (its error output still reaches the log).
/// </summary>
internal sealed class FunctionDocker(IPipelineFunctionTools tools, PipelineFunctionContext context)
{
    private const string StderrPrefix = "[stderr] ";
    private const string ExitLinePrefix = "[process] exit-code=";

    private readonly string _executable = FunctionShell.Tool(context, "docker");

    /// <summary><c>command -v docker</c>.</summary>
    public bool IsAvailable => FunctionShell.FindOnPath(context, "docker") is not null;

    /// <summary>The non-empty lines Docker printed on its standard output, or null when it failed. Its
    /// error output reaches the step log, as a command substitution leaves it.</summary>
    public async Task<IReadOnlyList<string>?> LinesAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var lines = new List<string>();
        var result = await tools.RunAsync(StartInfo(arguments), (line, level) =>
        {
            if (line.StartsWith(StderrPrefix, StringComparison.Ordinal))
                return context.Log(line, level);
            if (!line.StartsWith(ExitLinePrefix, StringComparison.Ordinal) && line.Trim().Length > 0)
                lock (lines)
                    lines.Add(line.Trim());
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut ? lines : null;
    }

    /// <summary>Runs Docker with its output in the step log; its exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await tools.RunAsync(StartInfo(arguments), (line, level) => context.Log(line, level), cancellationToken)
            .ConfigureAwait(false);
        return result.TimedOut ? -1 : result.ExitCode;
    }

    /// <summary>Runs Docker with its standard output discarded (<c>&gt;/dev/null</c>), and its error
    /// output too unless <paramref name="keepErrors"/> (<c>&gt;/dev/null 2&gt;&amp;1</c>); its exit code.</summary>
    public async Task<int> QuietAsync(CancellationToken cancellationToken, string[] arguments, bool keepErrors = true)
    {
        var result = await tools.RunAsync(
            StartInfo(arguments),
            (line, level) => keepErrors && line.StartsWith(StderrPrefix, StringComparison.Ordinal) ? context.Log(line, level) : Task.CompletedTask,
            cancellationToken).ConfigureAwait(false);
        return result.TimedOut ? -1 : result.ExitCode;
    }

    private ProcessStartInfo StartInfo(string[] arguments) =>
        FunctionShell.StartInfo(context, _executable, context.Workspace ?? Environment.CurrentDirectory, arguments);
}
