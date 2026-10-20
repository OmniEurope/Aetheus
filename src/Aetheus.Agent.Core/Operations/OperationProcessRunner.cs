// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Operations;

internal static class OperationProcessRunner
{
    public static Task<ExecutorResult> RunOptionalAsync(
        ProcessStartInfo? startInfo,
        Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, ILogger, CancellationToken, Task<ExecutorResult>> run,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        ILogger logger,
        CancellationToken cancellationToken) =>
        startInfo is null
            ? Task.FromResult(new ExecutorResult(-1, false))
            : run(startInfo, timeoutSeconds, onOutput, logger, cancellationToken);
}
