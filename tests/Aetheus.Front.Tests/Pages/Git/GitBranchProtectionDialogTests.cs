// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class GitBranchProtectionDialogTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public GitBranchProtectionDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_FormFields()
    {
        var cut = Render<GitBranchProtectionDialog>(p => p.Add(x => x.RepoId, 1));

        Assert.Contains("Pattern", cut.Markup);
        Assert.Contains("PreventDeletion", cut.Markup);
        Assert.Contains("PreventForcePush", cut.Markup);
        // Decision D13: the three-level choice replaces the set-aside "require a pull request" switch.
        Assert.Contains("BranchProtectionLevel", cut.Markup);
        Assert.DoesNotContain("RequirePR", cut.Markup);
    }

    [Fact]
    public void Renders_SaveAndCancelButtons()
    {
        var cut = Render<GitBranchProtectionDialog>(p => p.Add(x => x.RepoId, 1));

        Assert.Contains("Save", cut.Markup);
        Assert.Contains("GoBack", cut.Markup);
    }

    [Fact]
    public void Renders_Checkboxes()
    {
        var cut = Render<GitBranchProtectionDialog>(p => p.Add(x => x.RepoId, 1));

        var checkboxes = cut.FindAll("input[type='checkbox']");
        // Prevent deletion and prevent force-push; the protection level is a list, not a checkbox (D13).
        Assert.Equal(2, checkboxes.Count);
    }

    [Fact]
    public async Task Save_WithEmptyPattern_DoesNotPost()
    {
        var cut = Render<GitBranchProtectionDialog>(p => p.Add(x => x.RepoId, 1));

        // Pattern left blank: Save short-circuits with a validation toast and never calls the API.
        var save = typeof(GitBranchProtectionDialog).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)save.Invoke(cut.Instance, [])!);

        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("branch-protection"));
    }

    [Fact]
    public async Task Save_WithPattern_PostsRuleToBranchProtectionEndpoint()
    {
        _handler.SetJsonResponse("api/git/repos/1/branch-protection",
            new BranchProtectionRuleDto { Id = 3, Pattern = "main" });

        var cut = Render<GitBranchProtectionDialog>(p => p.Add(x => x.RepoId, 1));

        typeof(GitBranchProtectionDialog)
            .GetField("_pattern", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, "main");

        var save = typeof(GitBranchProtectionDialog).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)save.Invoke(cut.Instance, [])!);

        // Save POSTs the rule to the branch-protection endpoint. (The dialog-close-with-true on success is
        // a OmniDialogService.Close a standalone bUnit render cannot observe.)
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/git/repos/1/branch-protection"));
    }

    [Fact]
    public void D13_OffersTheThreeProtectionLevels_NotProtectedFirstAndSelected()
    {
        var cut = Render<GitBranchProtectionDialog>(p => p.Add(x => x.RepoId, 1));

        var select = cut.Find("#oe-pages-git-gitbranchprotectiondialog-2");
        var options = select.QuerySelectorAll("option").Select(o => o.TextContent.Trim()).ToList();
        Assert.Equal(
            [
                "Enum_BranchProtectionLevel_NotProtected",
                "Enum_BranchProtectionLevel_ProtectedExceptPipelines",
                "Enum_BranchProtectionLevel_StrictlyProtected"
            ],
            options);
        Assert.Equal("Enum_BranchProtectionLevel_NotProtected", SelectedText(select).Trim());
    }

    [Fact]
    public async Task D13_Save_PostsTheChosenProtectionLevel()
    {
        _handler.SetJsonResponse("api/git/repos/1/branch-protection",
            new BranchProtectionRuleDto { Id = 3, Pattern = "main", ProtectionLevel = BranchProtectionLevel.StrictlyProtected });
        var cut = Render<GitBranchProtectionDialog>(p => p.Add(x => x.RepoId, 1));
        typeof(GitBranchProtectionDialog)
            .GetField("_pattern", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, "main");

        var select = cut.Find("#oe-pages-git-gitbranchprotectiondialog-2");
        var strict = select.QuerySelectorAll("option")
            .Single(o => o.TextContent.Contains("StrictlyProtected", StringComparison.Ordinal));
        select.Change(strict.GetAttribute("value"));
        var save = typeof(GitBranchProtectionDialog).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)save.Invoke(cut.Instance, [])!);

        var post = Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("branch-protection"));
        Assert.Equal(BranchProtectionLevel.StrictlyProtected, Read<CreateBranchProtectionRuleRequest>(post.Body).ProtectionLevel);
    }

    // The body as the API reads it (camelCase, an enum as its name or its number).
    private static T Read<T>(string? body) => System.Text.Json.JsonSerializer.Deserialize<T>(body!,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;

    private static string SelectedText(AngleSharp.Dom.IElement select) =>
        ((AngleSharp.Html.Dom.IHtmlSelectElement)select).SelectedOptions.FirstOrDefault()?.TextContent ?? string.Empty;
}
