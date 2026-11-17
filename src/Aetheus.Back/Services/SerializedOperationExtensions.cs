// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services;

/// <summary>
/// Request-side shapes of <see cref="IPostgresLeaderLease.RunSerializedAsync"/> (ADR-029), the only place that issues
/// advisory-lock SQL. The protected work, its save included, runs inside the operation, so the next caller of the
/// same name reads the committed result. The lock is taken on its own connection, so it must never wrap work that
/// joins an outer transaction committed after the operation returns.
/// </summary>
internal static class SerializedOperationExtensions
{
    /// <summary>Runs <paramref name="operation"/> under the named lock when <paramref name="serialize"/> is true,
    /// directly otherwise, and returns its result.</summary>
    internal static async Task<T> RunSerializedWhenAsync<T>(
        this IPostgresLeaderLease lease, bool serialize, string operationName,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(operation);
        if (!serialize) return await operation(cancellationToken).ConfigureAwait(false);

        T result = default!;
        await lease.RunSerializedAsync(
            operationName,
            async ct => result = await operation(ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Runs <paramref name="operation"/> under the named lock when <paramref name="serialize"/> is true,
    /// directly otherwise.</summary>
    internal static Task RunSerializedWhenAsync(
        this IPostgresLeaderLease lease, bool serialize, string operationName,
        Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(operation);
        return serialize
            ? lease.RunSerializedAsync(operationName, operation, cancellationToken)
            : operation(cancellationToken);
    }
}
