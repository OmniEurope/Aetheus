// SPDX-License-Identifier: EUPL-1.2
using AngleSharp.Dom;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// SITE-FUNC-HOME-CAPABILITY-001: in the browser a click (or Enter/Space) on a control nested in a
/// clickable grid row bubbles to the row, whose handler runs after the control's and wins the
/// navigation. bUnit cannot show it: its bubbling collects the row's handler id before the control's
/// handler re-renders the grid, then silently ignores the stale id. So the guarantee is read from the
/// rendered DOM instead: between the control and its row, some element must stop the event.
/// </summary>
internal static class RowEventIsolation
{
    internal static void AssertRowDoesNotReceive(IElement control, params string[] eventNames)
    {
        ArgumentNullException.ThrowIfNull(control);
        var row = control.Closest("tr") ?? throw new InvalidOperationException("The control is not inside a grid row.");
        Assert.True(row.HasAttribute("blazor:onclick"), "The row is not clickable, so this check proves nothing.");
        foreach (var eventName in eventNames)
        {
            var stopped = false;
            for (var current = control; current is not null && current != row; current = current.ParentElement)
            {
                if (current.HasAttribute($"blazor:{eventName}:stopPropagation"))
                {
                    stopped = true;
                    break;
                }
            }

            Assert.True(stopped, $"'{eventName}' on <{control.TagName.ToLowerInvariant()}> bubbles to its grid row: {control.OuterHtml}");
        }
    }

    /// <summary>The selector of the rows a click or Enter/Space activates.</summary>
    internal const string ClickableRows = @"tr[blazor\:onclick]";

    /// <summary>
    /// Checks click and keydown for every link, button and form control in the given rows (pass
    /// <c>cut.FindAll(RowEventIsolation.ClickableRows)</c>), and returns how many it checked so a caller
    /// can prove the controls it meant to cover were rendered.
    /// </summary>
    internal static int AssertNoRowControlReachesTheRow(IEnumerable<IElement> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var controls = rows
            .SelectMany(row => row.QuerySelectorAll("a[href], button, input, select, textarea")
                .Where(control => control.Closest("tr") == row))
            .ToList();
        foreach (var control in controls)
            AssertRowDoesNotReceive(control, "onclick", "onkeydown");
        return controls.Count;
    }
}
