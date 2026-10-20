// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Help;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using HelpArticlePage = Aetheus.Front.Components.Help.HelpArticle;
using HelpArticleService = Aetheus.Front.Components.Shared.HelpArticle;

namespace Aetheus.Front.Tests.Pages.Help;

public class HelpArticleDeepTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public HelpArticleDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // Stub help JSON so HelpService doesn't throw JsonException
        _handler.SetJsonResponse("help/help-", new List<HelpArticleService>
        {
            new HelpArticleService("servers", "Servers", "Manage servers", "server", []),
            new HelpArticleService("pipelines", "Pipelines", "CI/CD pipelines", "pipeline", [])
        });
    }

    [Fact]
    public void Renders_HelpArticle_WithUnknownKey_RedirectsToHelp()
    {
        var cut = Render<HelpArticlePage>(p => p.Add(x => x.PageKey, "unknown-page-xyz"));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        // An unknown article key resolves to no article and redirects back to the help index.
        Assert.Null(typeof(HelpArticlePage).GetField("_article", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance));
        Assert.Contains("help", nav.Uri);
    }

    [Fact]
    public void Renders_HelpArticle_WithKnownKey_Servers()
    {
        var cut = Render<HelpArticlePage>(p => p.Add(x => x.PageKey, "servers"));
        cut.WaitForState(() =>
        {
            var art = typeof(HelpArticlePage).GetField("_article", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
            return art != null;
        });

        // The known "servers" key resolves to the matching article (loaded from the stubbed help JSON).
        var article = (HelpArticleService?)typeof(HelpArticlePage)
            .GetField("_article", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.NotNull(article);
        Assert.Equal("servers", article!.Key);
    }

    [Fact]
    public void PageKey_Parameter_IsSet()
    {
        var cut = Render<HelpArticlePage>(p => p.Add(x => x.PageKey, "pipelines"));
        Assert.Equal("pipelines", cut.Instance.PageKey);
    }

    [Fact]
    public void PageRoutes_ContainsExpectedKeys()
    {
        var field = typeof(HelpArticlePage).GetField("PageRoutes", BindingFlags.NonPublic | BindingFlags.Static)!;
        var routes = (Dictionary<string, string>)field.GetValue(null)!;
        Assert.True(routes.ContainsKey("dashboard"));
        Assert.True(routes.ContainsKey("servers"));
        Assert.True(routes.ContainsKey("pipelines"));
    }

    [Fact]
    public void OnParametersSetAsync_WithEmptyKey_NavigatesToHelp()
    {
        var cut = Render<HelpArticlePage>(p => p.Add(x => x.PageKey, ""));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.Contains("help", nav.Uri);
    }

    [Fact]
    public void Article_IsNull_AfterInit_WhenKeyNotFound()
    {
        var cut = Render<HelpArticlePage>(p => p.Add(x => x.PageKey, "nonexistent-page-abc"));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        // With stubbed help data, a nonexistent key leaves _article null and redirects to /help.
        Assert.Null(typeof(HelpArticlePage).GetField("_article", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance));
        Assert.Contains("help", nav.Uri);
    }
}
