// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Tells the menu that a project's grade may have moved (a finding decided or reopened), so its Quality badges re-read
/// the grade at once instead of keeping the letter read when the page was entered. The menu and the project overview read
/// the same grade; without this signal the menu kept the old letter until the next navigation to a quality page.
/// </summary>
public sealed class QualityChangeNotifier
{
    public event Action? OnChanged;

    public void Notify() => OnChanged?.Invoke();
}
