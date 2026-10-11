// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

internal static class TaskQueuePersistence
{
    internal static async Task PersistAndNotifyAsync(
        Func<ServerTask, CancellationToken, Task> persist,
        ITaskService taskService,
        ServerTask task,
        CancellationToken ct)
    {
        // SITE-OFFLINE-001: the one place the module services (Apache, Certbot, Cron, Docker, Mail,
        // Portsentry, Rkhunter, Teamspeak, server configurations) queue a task, so the offline refusal
        // of CreateTask/CreateOperation holds for them too, before anything is persisted.
        await taskService.EnsureServerOnlineAsync(task.ServerId, ct).ConfigureAwait(false);
        await persist(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);
    }
}
