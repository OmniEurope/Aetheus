// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Site audit 2026-10-05, mobile findings (SITE-MOB-001 to 004, 007, 008), measured at 390px: rows of actions
/// and fields laid out by a horizontal <c>OmniStack</c> stayed on one line, so their buttons overlapped or ran
/// past the phone's content area, and two app rules lost to OE's code rule. These guards read the sources;
/// the layout itself is measured by <c>Aetheus.E2E.MobileActionBarLayoutTests</c>.
/// </summary>
public sealed class MobileLayoutAuditTests
{
    private static readonly string Components = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Components");

    private static readonly string Css = Regex.Replace(
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css")),
        @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static readonly Regex StackTag = new(@"<(?<close>/)?OmniStack\b(?<attrs>[^>]*?)(?<self>/)?>", RegexOptions.Compiled);

    /// <summary>Each row is identified by a piece of its content found nowhere else in the file.</summary>
    public static TheoryData<string, string> ActionBars => new()
    {
        { "Dashboards/DashboardEdit.razor", "MoveUp(widget)" },
        { "Notifications/NotificationsAdmin.razor", "@L[\"NotificationChannels\"]</OmniHeading>" },
        { "Notifications/NotificationsAdmin.razor", "OpenChannelDialogAsync(null)" },
        { "Notifications/NotificationsAdmin.razor", "@L[\"Rules\"]</OmniHeading>" },
        { "Notifications/NotificationsAdmin.razor", "OpenRuleDialogAsync(null)" },
        { "Servers/ServerDetailSections/DockerRelationsGraph.razor", "ResetViewAsync" },
        { "PackageFeeds/PackageFeedPackagesDialog.razor", "OnClick=\"@SyncAsync\"" },
        { "Servers/ServerDetailSections/ServerConfigSection.razor", "PreviewConfigAsync" },
        { "Pipelines/PipelineEdit.razor", "OmniIconName.Add\" /></Icon><ChildContent><span>@L[\"Create\"]" },
        { "Servers/ServerDetailSections/DockerProjectDialog.razor", "DockerContainerAction.Restart" },
        { "Projects/ProjectFormDialog.razor", "OnClick=\"@OnDelete\"" },
        { "Servers/ContactAgentDialog.razor", "CancelAndCloseAsync" },
        { "Servers/ServerDetailSections/DockerImagesTab.razor", "PullImageAsync" },
        { "AiTasks/AiTasks.razor", "OpenDialogAsync(null)" },
        { "Organizations/OrganizationDetail.razor", "OnAddMember" },
        { "Users/UserEdit.razor", "OnAddOrg" },
        { "Users/UserEdit.razor", "Busy=\"@(_changingPassword)\"" },
        { "Shared/AppIngestionSettings.razor", "OmniLabel For=\"analytics-threshold-metric\"" },
        { "Projects/ProjectDetailSections/ProjectBoardSection.razor", "_assigneeFilter" },
        { "Projects/ProjectDetailSections/ProjectBoardSection.razor", "OnClick=\"@OpenCreateAsync\"" },
        { "Users/RoleEdit.razor", "OnApplyBulk" },
        { "Users/RoleEdit.razor", "OnAddUser" },
        { "Pipelines/StageEditPanel.razor", "OnAddVariable" },
        { "Pipelines/VisualPipelineEditor.razor", "OnAddPipelineVar" },
    };

    [Theory]
    [MemberData(nameof(ActionBars))]
    public void APhoneWideActionRow_WrapsOntoFurtherLines(string file, string anchor)
    {
        var source = File.ReadAllText(Path.Combine(Components, file));
        var at = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{file}: anchor '{anchor}' not found, the guard would protect nothing.");
        Assert.Equal(-1, source.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal));

        var attributes = InnermostHorizontalStackAround(source, at);
        Assert.True(attributes is not null, $"{file}: '{anchor}' is not inside a horizontal OmniStack any more.");
        Assert.Matches(@"\bWrap=""true""", attributes);
    }

    [Theory]
    [InlineData("api-ref-path")]
    [InlineData("analysis-finding-prompt-source")]
    public void ALongCodeLine_BreaksInsideItsBox_OverOeCodeRule(string className)
    {
        // OE 1.6.0: .omni-theme-scope :is(pre, code, kbd, samp) { overflow-wrap: normal }, specificity (0,1,1).
        // A rule of the app breaks the line only when it is more specific; app.css loading later does not help
        // a single class (0,1,0).
        var winning = Regex.Matches(Css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .Where(rule => Regex.IsMatch(rule.Groups["body"].Value, @"overflow-wrap\s*:\s*anywhere"))
            .SelectMany(rule => rule.Groups["selector"].Value.Split(','))
            .Select(selector => selector.Trim())
            .Where(selector => Regex.IsMatch(selector, $@"\.{Regex.Escape(className)}$"))
            .Where(selector => Specificity(selector).CompareTo((0, 1, 1)) > 0)
            .ToList();

        Assert.NotEmpty(winning);
    }

    [Fact]
    public void TheVisualEditorVariableGrid_ShrinksToAPhone()
    {
        var grid = Regex.Match(Css, @"(?:^|\})\s*\.vp-var-grid\s*\{(?<body>[^}]*)\}");
        Assert.True(grid.Success);
        Assert.Matches(@"grid-template-columns\s*:\s*minmax\(0,\s*1fr\)\s+minmax\(0,\s*1fr\)\s+auto", grid.Groups["body"].Value);

        // OE gives a text box a 12rem minimum, (0,2,0) or (0,3,0) inside a row; the grid's boxes drop it.
        var box = Regex.Match(Css, @"(?:^|\})\s*\.vp-var-grid\s*>\s*\.omni-input\.omni-text-box\s*\{(?<body>[^}]*)\}");
        Assert.True(box.Success);
        Assert.Matches(@"min-inline-size\s*:\s*0\b", box.Groups["body"].Value);
    }

    private static string? InnermostHorizontalStackAround(string source, int position)
    {
        var open = new Stack<(int Start, string Attributes)>();
        foreach (Match tag in StackTag.Matches(source))
        {
            if (tag.Index > position) break;
            if (tag.Groups["self"].Success) continue;
            if (tag.Groups["close"].Success) open.Pop();
            else open.Push((tag.Index, tag.Groups["attrs"].Value));
        }

        return open.Select(entry => entry.Attributes)
            .FirstOrDefault(attributes => attributes.Contains("OmniOrientation.Horizontal", StringComparison.Ordinal));
    }

    private static (int Ids, int Classes, int Elements) Specificity(string selector)
    {
        var ids = Regex.Matches(selector, @"#[\w-]+").Count;
        var classes = Regex.Matches(selector, @"\.[\w-]+|\[[^\]]+\]|:(?!:)[\w-]+").Count;
        var elements = Regex.Matches(selector, @"(?:^|[\s>+~])[a-zA-Z][\w-]*").Count;
        return (ids, classes, elements);
    }
}
