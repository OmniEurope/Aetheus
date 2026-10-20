// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary> An executor whose work reads the caller's environment variables. The call without them
/// is the same call with none: it is written once here rather than repeated in every executor.
/// </summary>
public abstract class EnvironmentOperationExecutor : IOperationExecutor
{
    public abstract bool CanHandle(OperationKind kind);

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken) =>
        ExecuteAsync(kind, target, new Dictionary<string, string>(), timeoutSeconds, onOutput, cancellationToken);

    public abstract Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken);
}
