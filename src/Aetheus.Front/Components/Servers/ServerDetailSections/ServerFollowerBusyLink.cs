// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R-12: ties a section to its <see cref="ServerTaskFollower"/> so the section's actions are disabled while
/// the follower holds a task the page adopted on arrival (<see cref="ServerTaskFollower.Busy"/> covers only that: a
/// task the page launched itself is covered by the page's own "launching" state). The section sets
/// <see cref="Follower"/> from its <c>@ref</c> and re-renders when the follower's busy state changes.
/// </summary>
internal sealed class ServerFollowerBusyLink(Action rerender)
{
    private ServerTaskFollower? _follower;

    /// <summary>True while the follower holds a task adopted on arrival that veils the section.</summary>
    public bool Busy => _follower?.Busy ?? false;

    public ServerTaskFollower? Follower
    {
        get => _follower;
        set
        {
            if (ReferenceEquals(_follower, value)) return;
            if (_follower is not null) _follower.BusyChanged -= rerender;
            _follower = value;
            if (value is null) return;
            value.BusyChanged += rerender;
            // The follower may have adopted a task while it initialized, before this reference existed.
            if (value.Busy) rerender();
        }
    }
}
