// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// R-36: waiting is veiled at three nested levels, never with a logo of its own. Level 1 is the boot splash over the whole
/// window, level 2 the veil of the content area (<c>MainLayout</c>, held by <c>VeilHold</c> / <c>BusyVeil</c>), level 3 a
/// <c>ZoneVeil</c> over one component. A page, a section or a dialog that waits for its data never renders
/// <c>AetheusLoader</c> (a second logo that moves and changes size between the levels, the defect of R-36): the loader
/// stays only where it is a running-task indicator or a card of its own, listed in <see cref="Allowed"/> with its reason.
/// The three levels also draw the same plane in the same size, which the stylesheet must not undo.
/// </summary>
public sealed class LoadingLevelGuardTests
{
    private static readonly string Front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

    /// <summary>The files that may render <c>AetheusLoader</c>, with the reason. A stale entry fails.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Layout/TaskTrackerWidget.razor"] = "a small indicator beside each running task of the task list; it is progress, not a wait for data",
        ["Layout/RealtimeSyncIndicator.razor"] = "the realtime catch-up card shown when the page returns to the foreground, a card of its own over a loaded page",
        ["Components/Servers/ServerDetailSections/ServerTaskFollower.razor"] = "the progress block of a running agent task (R-19, handled by R-37)",
    };

    [Fact]
    public void NoFileRendersAnAetheusLoader_ToWaitForData_ExceptTheListedRunningTaskIndicators()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var file in RepositoryScan.Enumerate(Front, "*.razor"))
        {
            var relative = Path.GetRelativePath(Front, file).Replace('\\', '/');
            if (!Regex.IsMatch(File.ReadAllText(file), @"<AetheusLoader\b"))
                continue;
            if (Allowed.ContainsKey(relative))
                used.Add(relative);
            else
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "R-36: wait at one of the three levels (VeilHold for the content area, ZoneVeil for one component), not with a loader:\n  "
            + string.Join("\n  ", offenders));
        var stale = Allowed.Keys.Where(key => !used.Contains(key)).ToList();
        Assert.True(stale.Count == 0, "These allowed files no longer render a loader, remove them:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void ARoutedPage_NeverVeilsItsWholeFirstLoad_WithAZoneVeilAtItsRoot()
    {
        // A page that has nothing yet holds the veil of the content area (level 2); a ZoneVeil over a routed page's
        // first read would leave the content area unveiled around it. Pages under a detail layout (header and tabs
        // already drawn) are the listed exceptions: their layout is loaded, only the section waits (level 3).
        var underLayout = new[] { "Components/Servers/Sections/Docker.razor" };
        var offenders = new List<string>();
        foreach (var file in RepositoryScan.Enumerate(Front, "*.razor"))
        {
            var source = File.ReadAllText(file);
            if (!Regex.IsMatch(source, @"(?m)^@page\b"))
                continue;
            var relative = Path.GetRelativePath(Front, file).Replace('\\', '/');
            // The root-level branch of a page: "@if (_loading)\n{\n    <ZoneVeil" at column 0.
            if (Regex.IsMatch(source, @"(?m)^(?:@if|else if) \([^)]*\)\s*\r?\n\{\s*\r?\n\s*<ZoneVeil\b")
                && !underLayout.Contains(relative))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "R-36: a routed page that has nothing yet holds the content-area veil (<VeilHold Busy=\"true\" />):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ThePlaneIsTheSameSize_AtTheThreeLevels_AndTheContentVeilKeepsItWhereTheSplashHasIt()
    {
        var css = File.ReadAllText(Path.Combine(Front, "wwwroot", "css", "app.css"));

        // Level 1: the splash plane is 6rem, OE's default size of the plane of a veil (levels 2 and 3) unless overridden.
        Assert.Matches(@"\.app-splash-logo\s*\{[^}]*width:\s*6rem", css);
        Assert.DoesNotContain("--omni-page-veil-logo-size", css, StringComparison.Ordinal);

        // Level 2: the plane sits at the middle of the window, as the splash's does.
        var rule = Regex.Match(css, @"\.omni-page-veil\.site-content-veil\s+\.omni-page-veil__logo\s*\{(?<body>[^}]*)\}");
        Assert.True(rule.Success, "The content-area veil must keep its plane at the middle of the window (site-content-veil).");
        Assert.Contains("position: fixed", rule.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("translate(-50%, -50%)", rule.Groups["body"].Value, StringComparison.Ordinal);

        // The splash itself stays centred in the window.
        Assert.Matches(@"\.app-splash\s*\{[^}]*align-items:\s*center;[^}]*justify-content:\s*center", css);
        var layout = File.ReadAllText(Path.Combine(Front, "Layout", "MainLayout.razor"));
        Assert.Matches(@"<OmniPageVeil\b[^>]*\bClass=""site-content-veil""", layout);
    }
}
