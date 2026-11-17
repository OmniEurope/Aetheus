// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary> The actions a project section adds to the "..." menu of the project header, next to
/// Follow and Edit (the findings export of the quality page), instead of a row of its own above its
/// content. <see cref="ProjectDetailLayout"/> owns one and cascades it to the section it hosts.
/// </summary>
public sealed class ProjectSectionMenu
{
    private IReadOnlyList<ProjectSectionMenuAction> _actions = [];

    public IReadOnlyList<ProjectSectionMenuAction> Actions => _actions;

    /// <summary>Raised when the actions change, for the header to render them.</summary>
    public event Action? Changed;

    /// <summary>
    /// Replaces the section's actions. Setting the same actions again (equal records, the section
    /// keeping one delegate per action) raises nothing, so a section may publish from any lifecycle
    /// step without starting a render loop.
    /// </summary>
    public void Set(IReadOnlyList<ProjectSectionMenuAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (_actions.SequenceEqual(actions)) return;
        _actions = actions;
        Changed?.Invoke();
    }

    public void Clear() => Set([]);

    /// <summary>
    /// True when the header's Settings button is the page's main action (blue) because the section moved its
    /// own action into the menu (the monitoring page, R-32).
    /// </summary>
    public bool SettingsPrimary { get; private set; }

    public void SetSettingsPrimary(bool primary)
    {
        if (SettingsPrimary == primary) return;
        SettingsPrimary = primary;
        Changed?.Invoke();
    }
}
