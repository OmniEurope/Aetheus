// SPDX-License-Identifier: EUPL-1.2

using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Tests;

/// <summary>
/// Recette R3-41/R3-42: a zone shows one main action and keeps the others in a "More actions" menu,
/// whose entries only exist once the menu is open. These helpers open the menus the way a user does.
/// </summary>
internal static class MenuTestExtensions
{
    /// <summary>
    /// Opens the overflow menus one after the other (a disabled trigger stays shut) and returns the
    /// first menu entry whose names contain <paramref name="label"/>, or null when no opened menu has it.
    /// </summary>
    internal static IElement? FindMenuItem<TComponent>(this IRenderedComponent<TComponent> cut, string label)
        where TComponent : IComponent
    {
        IElement? Find() => cut.FindAll(".omni-menu__item")
            .FirstOrDefault(entry => entry.Names().Contains(label, StringComparison.Ordinal));

        // A menu left open by an earlier call already lists its entries; clicking its trigger again would close it.
        if (Find() is { } open) return open;
        var count = cut.FindAll(".omni-overflow-menu__trigger").Count;
        for (var index = 0; index < count; index++)
        {
            if (cut.FindAll(".omni-overflow-menu__trigger")[index].HasAttribute("disabled")) continue;
            cut.FindAll(".omni-overflow-menu__trigger")[index].Click();
            if (Find() is { } item) return item;
            cut.FindAll(".omni-overflow-menu__trigger")[index].Click();
        }

        return null;
    }
}
