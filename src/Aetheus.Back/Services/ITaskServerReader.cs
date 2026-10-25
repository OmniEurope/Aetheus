// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services;

/// <summary>
/// Narrow read port resolving which server a task runs on. It lets the SignalR <c>LogHub</c> authorize
/// a task's log group without depending on the whole task service of the Tasks module.
/// </summary>
public interface ITaskServerReader
{
    /// <summary>The server the task is assigned to, or null for an unknown task.</summary>
    Task<int?> GetTaskServerIdAsync(int taskId, CancellationToken ct = default);
}
