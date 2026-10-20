// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R3-37: how a server page launches a task it follows. The follower is told before the request
/// leaves, and a launch the backend refused leaves nothing to follow. The page keeps its own toasts.
/// </summary>
internal static class ServerTaskLaunch
{
    /// <summary>Returns whether the backend queued the task.</summary>
    public static async Task<bool> FollowAsync(ServerTaskFollower? follower, string title, Func<Task<ApiStatus>> launch)
    {
        follower?.Expect(title);
        if ((await launch()).Success) return true;
        follower?.Forget();
        return false;
    }
}
