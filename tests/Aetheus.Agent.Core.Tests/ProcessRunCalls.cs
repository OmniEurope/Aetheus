// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Executors;
using Microsoft.Extensions.Logging;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Stands in for the process runner of an operation executor: records the program, the exact argv and
/// the timeout of every call, answers each call with the result chosen for it, and lets a call emit output
/// lines the way the real runner forwards a process's stdout and stderr.</summary>
internal sealed class ProcessRunCalls(Func<int, ExecutorResult>? resultFor = null, IReadOnlyList<string>? outputLines = null)
{
    public List<ProcessRun> Runs { get; } = [];

    public Task<ExecutorResult> RunAsync(
        ProcessStartInfo startInfo, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput,
        ILogger logger, CancellationToken cancellationToken) =>
        Record(startInfo.FileName, [.. startInfo.ArgumentList], timeoutSeconds, onOutput);

    /// <summary>The same recording for the runners that receive a bare argv (sudo is implied).</summary>
    public Task<ExecutorResult> RunSudoAsync(
        IReadOnlyList<string> argv, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput,
        ILogger logger, CancellationToken cancellationToken) =>
        Record("sudo", [.. argv], timeoutSeconds, onOutput);

    private async Task<ExecutorResult> Record(
        string fileName, string[] argv, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput)
    {
        var index = Runs.Count;
        Runs.Add(new ProcessRun(fileName, argv, timeoutSeconds));
        foreach (var line in outputLines ?? [])
            await onOutput(line, TaskLogLevel.Info);
        return resultFor?.Invoke(index) ?? new ExecutorResult(0, false);
    }
}

internal sealed record ProcessRun(string FileName, string[] Argv, int TimeoutSeconds);

/// <summary>Collects the messages an executor reports through its <c>onOutput</c> callback.</summary>
internal sealed class OutputLog
{
    private readonly Lock _gate = new();
    private readonly List<(string Message, TaskLogLevel Level)> _lines = [];

    // A snapshot: a process streams stdout and stderr concurrently, so lines arrive from two threads.
    public IReadOnlyList<(string Message, TaskLogLevel Level)> Lines
    {
        get { lock (_gate) return [.. _lines]; }
    }

    public Task Write(string message, TaskLogLevel level)
    {
        lock (_gate) _lines.Add((message, level));
        return Task.CompletedTask;
    }

    public bool Has(string fragment, TaskLogLevel level) =>
        Lines.Any(l => l.Level == level && l.Message.Contains(fragment, StringComparison.Ordinal));
}
