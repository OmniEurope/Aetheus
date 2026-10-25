// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>One action of <see cref="ProjectSectionMenu"/> : a Material icon name, its label, an
/// optional tooltip, whether it is disabled, and what it runs.</summary>
public sealed record ProjectSectionMenuAction(string Icon, string Text, string? Title, bool Disabled, Func<Task> Run);
