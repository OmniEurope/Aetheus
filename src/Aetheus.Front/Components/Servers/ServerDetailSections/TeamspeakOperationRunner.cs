// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Runs one of the TeamSpeak section's fire-and-forget operations: the backend only queues a task, whose
/// result then surfaces in the task tracker. A queued task confirms with a "task created" toast naming the
/// operation; a refused request or an HTTP failure shows the generic "operation failed" toast. While the
/// agent is offline nothing is sent: <paramref name="blockedByOfflineAgent"/> says so and toasts it
/// (SITE-OFFLINE-001). Recette R3-37: when the page gives its <paramref name="follower"/>, the follower
/// shows what becomes of the queued task, named by the operation's label.
/// </summary>
internal sealed class TeamspeakOperationRunner(
    NotifyHelper toast, Func<bool> blockedByOfflineAgent, Func<ServerTaskFollower?>? follower = null)
{
    /// <summary>Returns whether the task was queued; <paramref name="onSuccess"/> runs before the toast,
    /// only when it was.</summary>
    public async Task<bool> RunOperationAsync(
        Func<Task<ApiStatus>> operation, string label, Action? onSuccess = null)
    {
        if (blockedByOfflineAgent()) return false;
        var taskFollower = follower?.Invoke();
        taskFollower?.Expect(label);
        try
        {
            var status = await operation();
            if (!status.Success)
            {
                taskFollower?.Forget();
                toast.Error("Error", "OperationFailed");
                return false;
            }

            onSuccess?.Invoke();
            toast.Notify(OmniSeverity.Success, "TaskCreated", label);
            return true;
        }
        catch (HttpRequestException)
        {
            taskFollower?.Forget();
            toast.Error("Error", "OperationFailed");
            return false;
        }
    }
}
