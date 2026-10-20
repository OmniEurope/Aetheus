// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Shared;

public class GitCommitGraphTests : BunitContext
{
    public GitCommitGraphTests() => BunitTestHelper.RegisterServices(this);

    private static List<GitLightCommitDto> Chain() =>
    [
        new() { Sha = "ccc333", ShortSha = "ccc333", Message = "merge", AuthorName = "alice",
                AuthorDate = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc),
                ParentShas = ["bbb222", "aaa111"], RefNames = ["main"] },
        new() { Sha = "bbb222", ShortSha = "bbb222", Message = "feature work", AuthorName = "bob",
                AuthorDate = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                ParentShas = ["aaa111"] },
        new() { Sha = "aaa111", ShortSha = "aaa111", Message = "init", AuthorName = "alice",
                AuthorDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ParentShas = [] }
    ];

    [Fact]
    public void Renders_Graph_WithSvgAndCommitMessages()
    {
        var cut = Render<GitCommitGraph>(p => p.Add(c => c.Commits, Chain()));

        Assert.NotEmpty(cut.FindAll("svg"));
        Assert.Equal(3, cut.FindAll("circle").Count);
        var edges = cut.FindAll("path");
        Assert.Equal(3, edges.Count);
        Assert.Contains(edges, edge => edge.GetAttribute("d")!.Contains('C'));
        Assert.Contains("main", cut.Markup);
        Assert.Contains("feature work", cut.Markup);
        Assert.Contains("init", cut.Markup);
    }

    [Fact]
    public void EachSha_LinksToItsCommitPage_WhenTheRepositoryIsKnown()
    {
        // The short SHA is the link to the commit.
        var cut = Render<GitCommitGraph>(p => p.Add(c => c.Commits, Chain()).Add(c => c.RepoId, 4));

        var links = cut.FindAll(".git-graph-sha a.short-id__text");
        Assert.Equal(
            ["/git-repositories/4/commits/ccc333", "/git-repositories/4/commits/bbb222", "/git-repositories/4/commits/aaa111"],
            links.Select(link => link.GetAttribute("href")));
        Assert.Equal("ccc333", links[0].TextContent);
    }

    [Fact]
    public void WithoutARepository_TheShaStaysText()
    {
        var cut = Render<GitCommitGraph>(p => p.Add(c => c.Commits, Chain()));

        Assert.Empty(cut.FindAll(".git-graph-sha a"));
        Assert.Equal(3, cut.FindAll(".git-graph-sha .short-id__text").Count);
    }

    [Fact]
    public void Renders_EmptyState_WhenNoCommits()
    {
        var cut = Render<GitCommitGraph>(p => p.Add(c => c.Commits, []));

        Assert.Empty(cut.FindAll("svg"));
        Assert.Contains("GitGraphEmptyTitle", cut.Markup);
    }
}
