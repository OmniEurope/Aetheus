// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Help;
using Bunit;
using HelpArticle = Aetheus.Front.Components.Shared.HelpArticle;
using HelpSection = Aetheus.Front.Components.Shared.HelpSection;

namespace Aetheus.Front.Tests.Pages.Help;

public class HelpCenterDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public HelpCenterDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // Stub the help JSON endpoint so HelpService doesn't get a parse error
        _handler.SetJsonResponse("help/help-", new List<HelpArticle>
        {
            new HelpArticle("servers", "Servers", "Manage servers", "server", []),
            new HelpArticle("pipelines", "Pipelines", "CI/CD pipelines", "pipeline", [])
        });
    }

    [Fact]
    public void Renders_HelpCenter_WithArticles()
    {
        var cut = Render<HelpCenter>();
        cut.WaitForState(() => !(bool)typeof(HelpCenter).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        // Init loads the two stubbed articles and surfaces their titles.
        var articles = (IReadOnlyList<HelpArticle>)typeof(HelpCenter).GetField("_articles", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, articles.Count);
        Assert.Contains("Servers", cut.Markup);
    }

    [Fact]
    public void Loading_IsFalse_AfterInit()
    {
        var cut = Render<HelpCenter>();
        cut.WaitForState(() =>
        {
            var loading = (bool)typeof(HelpCenter).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
            return !loading;
        });

        var loadingFinal = (bool)typeof(HelpCenter).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loadingFinal);
    }

    [Fact]
    public async Task FilterAsync_WithEmptySearch_ReturnsAll()
    {
        var cut = Render<HelpCenter>();
        cut.WaitForState(() => !(bool)typeof(HelpCenter).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        typeof(HelpCenter).GetField("_search", Priv)!.SetValue(cut.Instance, "");
        var method = typeof(HelpCenter).GetMethod("FilterAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        var articles = (IReadOnlyList<HelpArticle>)typeof(HelpCenter).GetField("_articles", Priv)!.GetValue(cut.Instance)!;
        var filtered = (IReadOnlyList<HelpArticle>)typeof(HelpCenter).GetField("_filtered", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(articles.Count, filtered.Count);
    }

    [Fact]
    public async Task FilterAsync_WithSearch_FiltersResults()
    {
        var cut = Render<HelpCenter>();
        cut.WaitForState(() => !(bool)typeof(HelpCenter).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        typeof(HelpCenter).GetField("_search", Priv)!.SetValue(cut.Instance, "xyznonexistent987");
        var method = typeof(HelpCenter).GetMethod("FilterAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        var filtered = (IReadOnlyList<HelpArticle>)typeof(HelpCenter).GetField("_filtered", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(filtered);
    }

    [Fact]
    public void Articles_And_Filtered_AreInitialized()
    {
        var cut = Render<HelpCenter>();
        cut.WaitForState(() => !(bool)typeof(HelpCenter).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        var articles = (IReadOnlyList<HelpArticle>?)typeof(HelpCenter).GetField("_articles", Priv)!.GetValue(cut.Instance);
        var filtered = (IReadOnlyList<HelpArticle>?)typeof(HelpCenter).GetField("_filtered", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(articles);
        Assert.NotNull(filtered);
    }

    [Fact]
    public void Search_Field_InitiallyNull()
    {
        var cut = Render<HelpCenter>();
        var search = (string?)typeof(HelpCenter).GetField("_search", Priv)!.GetValue(cut.Instance);
        Assert.Null(search);
    }
}
