// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;
using Aetheus.Front.Components.AiTasks;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Ai;

/// <summary>R3-13: the context files of an AI task are picked in the repository tree, one folder read at a
/// time, and listed with a remove button.</summary>
public sealed class AiContextFilePickerTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly BunitTestHelper.TestHandler _handler;
    private IReadOnlyList<string> _paths = [];

    public AiContextFilePickerTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetPaginatedJsonResponse("api/git/repos?", new List<GitLightRepoDto>
        {
            new() { Id = 7, ProjectId = 10, Name = "aetheus", Slug = "aetheus", DefaultBranch = "main" }
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/7/tree", new List<GitLightTreeEntryDto>
        {
            new() { Name = "docs", Path = "docs", Type = GitTreeEntryType.Tree },
            new() { Name = "AGENTS.md", Path = "AGENTS.md", Type = GitTreeEntryType.Blob },
            new() { Name = "README.md", Path = "README.md", Type = GitTreeEntryType.Blob }
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/7/tree?page=1&pageSize=100&sortDescending=false&ref=main&path=docs", new List<GitLightTreeEntryDto>
        {
            new() { Name = "guide.md", Path = "docs/guide.md", Type = GitTreeEntryType.Blob }
        });
    }

    private IRenderedComponent<AiContextFilePicker> RenderPicker() =>
        Render<AiContextFilePicker>(parameters => parameters
            .Add(picker => picker.ProjectId, 10)
            .Add(picker => picker.Paths, _paths)
            .Add(picker => picker.PathsChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<IReadOnlyList<string>>(
                this, paths => _paths = paths)));

    private static void ClickAdd(IRenderedComponent<AiContextFilePicker> cut) =>
        cut.Find("#ai-context-add").Click();

    private static void Pick(IRenderedComponent<AiContextFilePicker> cut, string text) =>
        cut.FindAll(".omni-tree__select").First(button => button.TextContent.Trim() == text).Click();

    [Fact]
    public void R3_13_TheTree_ListsFoldersBeforeFiles_AndAddsSeveralChoicesAtOnce()
    {
        var cut = RenderPicker();
        ClickAdd(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".omni-tree__select")));

        var texts = cut.FindAll(".omni-tree__select").Select(button => button.TextContent.Trim()).ToList();
        Assert.Equal(["docs", "AGENTS.md", "README.md"], texts);

        Pick(cut, "docs");
        Pick(cut, "AGENTS.md");
        cut.FindAll("button").Last(button => button.TextContent.Contains("Add", StringComparison.Ordinal)).Click();

        Assert.Equal(["docs/", "AGENTS.md"], _paths);
    }

    [Fact]
    public void R3_13_AFolder_LoadsItsContentWhenItOpens()
    {
        var cut = RenderPicker();
        ClickAdd(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".omni-tree__toggle")));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("path=docs", StringComparison.Ordinal));

        cut.Find(".omni-tree__toggle").Click();

        cut.WaitForAssertion(() => Assert.Contains(
            cut.FindAll(".omni-tree__select"), button => button.TextContent.Trim() == "guide.md"));
        Assert.Contains(_handler.Requests, request => request.Url.Contains("path=docs", StringComparison.Ordinal));
    }

    [Fact]
    public void R3_13_AChosenPath_CanBeRemovedFromTheList()
    {
        _paths = ["AGENTS.md", "docs/"];
        var cut = RenderPicker();

        Assert.Equal(2, cut.FindAll(".ai-context-item").Count);
        cut.FindAll(".ai-context-item button").First().Click();

        Assert.Equal(["docs/"], _paths);
    }

    [Fact]
    public void R3_13_TheAddButton_IsOffOnceThePathLimitIsReached()
    {
        _paths = Enumerable.Range(0, 32).Select(i => $"f{i}.md").ToList();
        var cut = RenderPicker();

        Assert.True(cut.Find("#ai-context-add").HasAttribute("disabled"));
    }

    [Fact]
    public void R3_13_AProjectWithoutRepository_SaysSo()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos?", new List<GitLightRepoDto>());
        var cut = RenderPicker();
        ClickAdd(cut);

        cut.WaitForAssertion(() => Assert.Contains("AiContextNoRepository", cut.Markup));
    }
}
