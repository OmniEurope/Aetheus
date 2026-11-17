// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// R-1: the application has ONE veil. <c>MainLayout</c> draws OE's <c>OmniPageVeil</c> (a constant colour,
/// the site's animated logo at its centre, laid over the content of the page) while the shared <c>BusyVeil</c> is busy;
/// an action goes through <c>BusyVeil.RunAsync</c> and a zone that waits declares <c>&lt;VeilHold Busy=...&gt;</c>.
/// No zone (page body, section body, dialog, card, table) veils itself with OE's control-level <c>omni-busy</c> class,
/// which pulses its opacity and outsets by 1px (the flicker and the second scrollbar of OE 1.10.0), and the
/// stylesheet carries no rule for it. A busy button keeps OE's <c>Busy</c> parameter (STD-BUSY), which is not this class.
/// </summary>
public sealed class SingleVeilGuardTests
{
    private static readonly string Front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

    [Theory]
    [InlineData("*.razor")]
    [InlineData("*.cs")]
    [InlineData("*.css")]
    public void NoZoneOfTheFront_VeilsItselfWithTheOmniBusyClass(string pattern)
    {
        var offenders = Sources(pattern)
            .Where(file => File.ReadAllText(file).Contains("omni-busy", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(Front, file).Replace('\\', '/'))
            .ToList();

        Assert.True(offenders.Count == 0,
            "R-1: a zone holds the one page veil (VeilHold, BusyVeil.RunAsync) instead of putting omni-busy on itself:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheMainLayout_DrawsTheContentAreaVeil_AndOnlyZoneVeilDrawsAnother_BothWithTheSitesPlane()
    {
        // R-36: two files draw OE's OmniPageVeil: the layout (level 2, the content area) and ZoneVeil (level 3, one component).
        var drawers = Sources("*.razor")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"<OmniPageVeil\b"))
            .Select(file => Path.GetRelativePath(Front, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Components/Shared/ZoneVeil.razor", "Layout/MainLayout.razor"], drawers);
        var layout = File.ReadAllText(Path.Combine(Front, "Layout", "MainLayout.razor"));
        Assert.Matches(@"<OmniPageVeil\b[^>]*\bBusy=""ContentVeilBusy""[^>]*><Logo><AetheusMark\b", layout);
        var layoutCode = File.ReadAllText(Path.Combine(Front, "Layout", "MainLayout.razor.cs"));
        Assert.Matches(@"ContentVeilBusy\s*=>\s*Veil\.IsBusy\s*\|\|\s*SessionBootstrapping", layoutCode);

        var zone = File.ReadAllText(Path.Combine(Front, "Components", "Shared", "ZoneVeil.razor"));
        Assert.Matches(@"<OmniPageVeil\b[^>]*\bScope=""OmniPageVeilScope\.Parent""", zone);
        Assert.Matches(@"<Logo><AetheusMark\b", zone);
    }

    private static IEnumerable<string> Sources(string pattern) =>
        RepositoryScan.Enumerate(Front, pattern)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
}
