// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Recette R-29: a server page's main action sits in the page header (the <c>PageHeaderActions</c> section the
/// server layout renders on the title line), with its menu beside it, never in a bar of the page body. A blue
/// (Primary) button of a server section page is therefore inside
/// <c>&lt;SectionContent SectionName="@PageHeaderActions.SectionName"&gt;</c>, or belongs to a smaller zone with
/// its own action: a tab (<c>OmniTabsItem</c>, its toolbar or form), a grid row (<c>Template</c>), or a form or
/// card that submits its own fields (<c>OmniTemplateForm</c>, <c>EditForm</c>, <c>OmniCard</c>).
/// </summary>
public sealed partial class ServerSectionHeaderActionGuardTests
{
    private const string SectionsDirectory = "src/Aetheus.Front/Components/Servers/ServerDetailSections";

    [Fact]
    public void AServerSectionPage_PutsItsMainActionInThePageHeader()
    {
        var files = RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, SectionsDirectory), "Server*Section.razor");
        Assert.True(files.Count >= 10, $"Only {files.Count} server section pages scanned: the scan is broken.");

        var offenders = files
            .SelectMany(file => PrimaryButtonsOutsideTheHeader(File.ReadAllText(file))
                .Select(line => $"{Path.GetFileName(file)}:{line}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Bouton principal hors de l'en-tête de page (recette R-29), à placer dans "
            + "<SectionContent SectionName=\"@PageHeaderActions.SectionName\"> : " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheScanner_FindsTheMainActionOfAStatusBar_AsTheApachePageHadIt()
    {
        // The Apache page before R-29: Restart (blue) and its menu at the end of the status bar.
        const string before = """
            <OmniRow Gap="OmniSpacing.Small" Align="OmniAlignment.Center" Class="omni-u-mb-lg">
                <OmniBadge Fill="OmniFill.Solid">@L["Installed"]</OmniBadge>
                <div class="blade-content-spacer"></div>
                <OmniButton Variant="OmniButtonVariant.Primary" Disabled="@(AgentOffline || _actionRunning)" OnClick="@(() => ShowConfirm(L["Restart"], L["ConfirmApacheRestart"], () => ExecuteActionAsync(ApacheAction.Restart)))"><Icon><OmniIcon Name="OmniIconName.Refresh" /></Icon><ChildContent><span>@L["Restart"]</span></ChildContent></OmniButton>
                <OmniOverflowMenu Label="@L["MoreActions"]"></OmniOverflowMenu>
            </OmniRow>
            """;
        const string after = """
            @* The main action <OmniButton Variant="OmniButtonVariant.Primary"> sits in the header. *@
            <SectionContent SectionName="@PageHeaderActions.SectionName">
                <OmniButton Variant="OmniButtonVariant.Primary" OnClick="@(() => ExecuteActionAsync(ApacheAction.Restart))"><Icon><OmniIcon Name="OmniIconName.Refresh" /></Icon></OmniButton>
            </SectionContent>
            <OmniTabsItem Key="sites"><OmniButton Variant="OmniButtonVariant.Primary" OnClick="@Add"></OmniButton></OmniTabsItem>
            <Template Context="row"><OmniButton Variant="OmniButtonVariant.Primary" OnClick="@(() => Edit(row))"></OmniButton></Template>
            <OmniSplitButton Variant="OmniButtonVariant.Secondary" Text="@L["Scan"]"></OmniSplitButton>
            """;

        Assert.Equal([4], PrimaryButtonsOutsideTheHeader(before));
        Assert.Empty(PrimaryButtonsOutsideTheHeader(after));
        Assert.Equal([1], PrimaryButtonsOutsideTheHeader("""<OmniSplitButton Variant="OmniButtonVariant.Primary" Text="@L["Scan"]"></OmniSplitButton>"""));
    }

    /// <summary>The 1-based lines of the blue buttons outside the header and outside a tab or a grid row.</summary>
    internal static IReadOnlyList<int> PrimaryButtonsOutsideTheHeader(string razor)
    {
        var source = RazorComment().Replace(razor, comment => new string(comment.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));
        var allowed = Zones(source, "SectionContent", requires: "PageHeaderActions.SectionName")
            .Concat(Zones(source, "OmniTabsItem"))
            .Concat(Zones(source, "Template"))
            .Concat(Zones(source, "OmniTemplateForm"))
            .Concat(Zones(source, "EditForm"))
            .Concat(Zones(source, "OmniCard"))
            .ToList();

        return ButtonStart().Matches(source)
            .Where(button => PrimaryVariant().IsMatch(StartTag(source, button.Index)))
            .Where(button => !allowed.Any(zone => zone.Start < button.Index && button.Index < zone.End))
            .Select(button => source[..button.Index].Count(c => c == '\n') + 1)
            .ToList();
    }

    // A start tag runs to its first child element: Razor attribute values hold lambdas ("=>") but no "<".
    private static string StartTag(string source, int start)
    {
        var next = source.IndexOf('<', start + 1);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static List<(int Start, int End)> Zones(string source, string tag, string? requires = null)
    {
        var zones = new List<(int Start, int End)>();
        var open = new Stack<(int Start, bool Counts)>();
        foreach (Match token in new Regex($@"<(/?){tag}\b", RegexOptions.None, TimeSpan.FromSeconds(1)).Matches(source))
        {
            if (token.Groups[1].Value.Length == 0)
            {
                open.Push((token.Index, requires is null || StartTag(source, token.Index).Contains(requires, StringComparison.Ordinal)));
            }
            else if (open.Count > 0)
            {
                var (start, counts) = open.Pop();
                if (counts) zones.Add((start, token.Index));
            }
        }

        Assert.True(open.Count == 0, $"Unbalanced <{tag}> in a server section page: the guard cannot read it.");
        return zones;
    }

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"<(OmniButton|OmniSplitButton)\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ButtonStart();

    [GeneratedRegex(@"\bVariant=""OmniButtonVariant\.Primary""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PrimaryVariant();
}
