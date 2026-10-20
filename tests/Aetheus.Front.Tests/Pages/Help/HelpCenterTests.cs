// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using HelpCenterPage = Aetheus.Front.Components.Help.HelpCenter;

namespace Aetheus.Front.Tests.Pages;

public class HelpCenterTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public HelpCenterTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_HelpCenterPage()
    {
        _handler.SetJsonResponse("help/help-", new List<HelpArticle>
        {
            new("servers", "Servers", "Manage servers", "rzi-server", [])
        });

        var cut = Render<HelpCenterPage>();
        cut.WaitForState(() => cut.Markup.Contains("Servers"));

        // The loaded article's title is rendered as a card.
        Assert.Contains("Servers", cut.Markup);
    }

    [Fact]
    public void Renders_ArticleList_WhenLoaded()
    {
        _handler.SetJsonResponse("help/help-", new List<HelpArticle>
        {
            new("servers", "Servers", "Manage servers", "rzi-server", []),
            new("pipelines", "Pipelines", "CI/CD pipelines", "rzi-pipeline", [])
        });

        var cut = Render<HelpCenterPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        Assert.Contains("Servers", cut.Markup);
        Assert.Contains("Pipelines", cut.Markup);
    }

    [Fact]
    public async Task FilterAsync_FiltersArticles()
    {
        _handler.SetJsonResponse("help/help-", new List<HelpArticle>
        {
            new("servers", "Servers", "Manage servers", "rzi-server", []),
            new("pipelines", "Pipelines", "CI/CD pipelines", "rzi-pipeline", [])
        });

        var cut = Render<HelpCenterPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        typeof(HelpCenterPage).GetField("_search", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, "servers");

        var method = typeof(HelpCenterPage).GetMethod("FilterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Searching "servers" narrows the filtered set to the matching article only (Pipelines drops out).
        var filtered = (IReadOnlyList<Aetheus.Front.Components.Shared.HelpArticle>)typeof(HelpCenterPage)
            .GetField("_filtered", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Single(filtered);
        Assert.Equal("servers", filtered[0].Key);
    }

    [Fact]
    public void Renders_SearchInput()
    {
        _handler.SetJsonResponse("help/help-", new List<HelpArticle>());

        var cut = Render<HelpCenterPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        var inputs = cut.FindAll("input");
        Assert.True(inputs.Count >= 1);
    }

    [Fact]
    public void SearchInput_FiltersArticles_WhileTyping()
    {
        _handler.SetJsonResponse("help/help-", new List<HelpArticle>
        {
            new("servers", "Servers", "Manage servers", "rzi-server", []),
            new("pipelines", "Pipelines", "CI/CD pipelines", "rzi-pipeline", [])
        });

        var cut = Render<HelpCenterPage>();
        cut.WaitForState(() => cut.Markup.Contains("Servers"));

        cut.Find("input").Input("servers");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Servers", cut.Markup);
            Assert.DoesNotContain("Pipelines", cut.Markup);
        });
    }
}
