// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class GitRepositoryDetailTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public GitRepositoryDetailTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupDefaultMocks()
    {
        _handler.SetJsonResponse("api/git/repos/1", new GitLightRepoDto
        {
            Id = 1,
            ProjectId = 1,
            ProjectName = "MyProject",
            Name = "my-repo",
            DefaultBranch = "main",
            CloneUrl = "http://localhost/git/my-repo.git",
            CreatedAt = DateTime.UtcNow
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/branches", new List<GitLightBranchDto>
        {
            new() { Name = "main", IsDefault = true, LastCommitSha = "abc1234" },
            new() { Name = "develop", IsDefault = false }
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/tags", new List<GitLightTagDto>
        {
            new() { Name = "v1.0.0", Sha = "def5678" }
        });
        _handler.SetJsonResponse("api/git/repos/1/commits", new PaginatedResult<GitLightCommitDto>
        {
            Items =
            [
                new GitLightCommitDto
                {
                    Sha = "abc123456789",
                    ShortSha = "abc1234",
                    Message = "Initial commit",
                    AuthorName = "Dev",
                    AuthorEmail = "dev@test.com",
                    AuthorDate = DateTime.UtcNow
                }
            ],
            TotalCount = 1
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/tree", new List<GitLightTreeEntryDto>
        {
            new() { Name = "src", Path = "src", Type = GitTreeEntryType.Tree },
            new() { Name = "README.md", Path = "README.md", Type = GitTreeEntryType.Blob, Size = 1024 }
        });
        _handler.SetJsonResponse("api/git/repos/1/pull-requests", new PaginatedResult<InternalPullRequestDto>
        {
            Items = [],
            TotalCount = 0
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/branch-protection", new List<BranchProtectionRuleDto>());
        _handler.SetJsonResponse("api/git/repos/1/graph", "");
        _handler.SetJsonResponse("api/git/repos/1/graph-data", new List<GitLightCommitDto>());
        _handler.SetJsonResponse("api/git/repos/1/blob", new GitLightBlobDto
        {
            Path = "README.md",
            Content = "# My Repo",
            Size = 10,
            IsBinary = false
        });
    }

    [Fact]
    public void Renders_RepoDetail()
    {
        SetupDefaultMocks();
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Contains("my-repo"));

        Assert.Contains("my-repo", cut.Markup);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheConfigurationRepository_IsMarkedByABadge_OnlyWhenItIsTheOne(bool configuration)
    {
        SetupDefaultMocks();
        _handler.SetJsonResponse("api/git/repos/1", new GitLightRepoDto
        {
            Id = 1,
            ProjectId = 1,
            Name = "my-repo",
            DefaultBranch = "main",
            CreatedAt = DateTime.UtcNow,
            IsConfiguration = configuration
        });

        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Contains("my-repo"));

        Assert.Equal(configuration, cut.FindAll("[data-testid='git-repo-configuration-badge']").Count == 1);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void TheMirrorOfAnExternalRepository_OffersNoConfigurationDesignation(bool mirror, int menus)
    {
        SetupDefaultMocks();
        _handler.SetJsonResponse("api/git/repos/1", new GitLightRepoDto
        {
            Id = 1,
            ProjectId = 1,
            Name = "my-repo",
            DefaultBranch = "main",
            CreatedAt = DateTime.UtcNow,
            IsAdditionalSource = mirror
        });

        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Contains("my-repo"));

        Assert.Equal(menus, cut.FindAll(".omni-page-header .omni-overflow-menu").Count);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R2002_TheCloneUrl_IsOneReadOnlyFieldThatCopiesItself_AndOnlyARefusedCopyToasts(bool accepted)
    {
        SetupDefaultMocks();
        var clipboard = JSInterop.SetupModule("./_content/OmniEurope.Blazor/omniInterop.js");
        clipboard.Setup<bool>("copyText", _ => true).SetResult(accepted);

        var cut = Render<GitRepositoryDetail>(parameters => parameters.Add(component => component.Id, 1));

        var field = cut.WaitForElement(".clone-url-input.omni-text-box-field--copy");
        var input = field.QuerySelector("input")!;
        Assert.True(input.HasAttribute("readonly"));
        Assert.Equal("http://localhost/git/my-repo.git", input.GetAttribute("value"));
        Assert.Equal("CloneUrl", input.GetAttribute("aria-label"));
        // OE's Copy button is welded to the field; the page adds no button of its own beside it.
        var copy = Assert.Single(field.QuerySelectorAll("button"));
        Assert.Contains("omni-text-box-field__copy", copy.ClassList);
        Assert.Null(field.ParentElement?.QuerySelector(".clone-url-input + .omni-button"));

        copy.Click();

        Assert.Equal("http://localhost/git/my-repo.git", Assert.Single(clipboard.Invocations["copyText"]).Arguments[0]);
        cut.WaitForAssertion(() =>
        {
            if (accepted)
                Assert.DoesNotContain(Services.Toasts(), toast => toast.Summary == "CopyFailed");
            else
                Assert.Contains(Services.Toasts(), toast => toast.Summary == "CopyFailed");
        });
    }

    [Fact]
    public void Commits_DefaultToAllBranches()
    {
        SetupDefaultMocks();

        var cut = Render<GitRepositoryDetail>(parameters => parameters.Add(component => component.Id, 1));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_handler.Requests, request =>
                request.Method == "GET"
                && request.Url.Contains("api/git/repos/1/commits", StringComparison.Ordinal)
                && (request.Url.Contains("ref=%2A", StringComparison.OrdinalIgnoreCase)
                    || request.Url.Contains("ref=*", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void RepositoryIdChange_ReloadsSameComponentInstance()
    {
        SetupDefaultMocks();
        _handler.SetJsonResponse("api/git/repos/2", new GitLightRepoDto
        {
            Id = 2,
            ProjectId = 1,
            ProjectName = "MyProject",
            Name = "second-repo",
            DefaultBranch = "main",
            CloneUrl = "http://localhost/git/second-repo.git",
            CreatedAt = DateTime.UtcNow
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/2/branches", new List<GitLightBranchDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/2/tags", new List<GitLightTagDto>());
        _handler.SetJsonResponse("api/git/repos/2/commits", new PaginatedResult<GitLightCommitDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/2/tree", new List<GitLightTreeEntryDto>());
        _handler.SetJsonResponse("api/git/repos/2/pull-requests", new PaginatedResult<InternalPullRequestDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/2/branch-protection", new List<BranchProtectionRuleDto>());
        _handler.SetJsonResponse("api/git/repos/2/graph-data", new List<GitLightCommitDto>());
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));

        cut.Render(p => p.Add(x => x.Id, 2));

        cut.WaitForAssertion(() => Assert.Contains("second-repo", cut.Markup));
        Assert.DoesNotContain("my-repo", cut.Markup);
    }

    [Fact]
    public void Renders_Branches()
    {
        SetupDefaultMocks();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/git-repositories/1?tab=branches");
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Contains("main"));

        Assert.Contains("main", cut.Markup);
    }

    [Fact]
    public void BranchDeepLink_FiltersTheServerQueryInsteadOfLoadingOnlyTheFirstPage()
    {
        SetupDefaultMocks();
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/git-repositories/1?tab=branches&branch=release%2F2026.08");

        Render<GitRepositoryDetail>(parameters => parameters.Add(component => component.Id, 1));

        Assert.Contains(_handler.Requests, request =>
            request.Method == "GET"
            && request.Url.Contains("api/git/repos/1/branches", StringComparison.Ordinal)
            && request.Url.Contains("search=release%2F2026.08", StringComparison.Ordinal));
    }

    [Fact]
    public void FileDeepLink_OpensTheRequestedBlobAtTheRequestedRevision()
    {
        SetupDefaultMocks();
        _handler.SetJsonResponse("api/git/repos/1/blob", new GitLightBlobDto
        {
            Path = "src/Sample.cs",
            Content = "line one\nline two\nline three",
            Size = 28,
            IsBinary = false
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/git-repositories/1?tab=files&ref=0123456789abcdef&path=src%2FSample.cs&line=2");

        var cut = Render<GitRepositoryDetail>(parameters => parameters.Add(component => component.Id, 1));

        cut.WaitForAssertion(() => Assert.Contains("src/Sample.cs", cut.Markup));
        Assert.Contains(_handler.Requests, request =>
            request.Method == "GET"
            && request.Url.Contains("api/git/repos/1/blob", StringComparison.Ordinal)
            && request.Url.Contains("ref=0123456789abcdef", StringComparison.Ordinal)
            && request.Url.Contains("path=src%2FSample.cs", StringComparison.Ordinal));
        Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "monacoInterop.initReadOnly"
            && invocation.Arguments.Count == 5
            && Equals(invocation.Arguments[4], 2));
    }

    [Fact]
    public void Renders_Loading_ThenContent()
    {
        SetupDefaultMocks();
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));

        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        Assert.Contains("my-repo", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyState_WhenRepoNotFound()
    {
        // An unsuccessful initial repository request must reach the page's empty state without
        // escaping to ErrorBoundary, allowing authentication redirection to complete.
        _handler.SetResponse("api/git/repos/999", System.Net.HttpStatusCode.InternalServerError);
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 999));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        Assert.Contains("NotFound", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyState_WhenProxyReturnsHtmlWithStatus200()
    {
        _handler.SetRawResponse("api/git/repos/999", "<html>upstream unavailable</html>");

        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 999));

        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        Assert.Contains("NotFound", cut.Markup);
    }

    [Theory]
    [InlineData("main", "main", true)]
    [InlineData("main", "develop", false)]
    [InlineData("main", "*", true)]
    [InlineData("release/v1.0", "release/*", true)]
    [InlineData("release/v1.0", "feature/*", false)]
    [InlineData("Main", "main", true)]
    public void BranchMatchesPattern_ReturnsExpected(string branchName, string pattern, bool expected)
    {
        var result = GitRepositoryViewHelpers.BranchMatchesPattern(branchName, pattern);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(500, " o")]
    [InlineData(1500, " Ko")]
    [InlineData(1_500_000, " Mo")]
    public void FormatSize_ReturnsExpected(long bytes, string expectedSuffix)
    {
        var result = GitRepositoryViewHelpers.FormatSize(bytes);
        Assert.EndsWith(expectedSuffix, result);
    }

    [Theory]
    [InlineData(".cs", "csharp")]
    [InlineData(".js", "javascript")]
    [InlineData(".ts", "typescript")]
    [InlineData(".json", "json")]
    [InlineData(".xml", "xml")]
    [InlineData(".yaml", "yaml")]
    [InlineData(".yml", "yaml")]
    [InlineData(".html", "html")]
    [InlineData(".css", "css")]
    [InlineData(".md", "markdown")]
    [InlineData(".py", "python")]
    [InlineData(".sh", "shell")]
    [InlineData(".ps1", "powershell")]
    [InlineData(".sql", "sql")]
    [InlineData(".dockerfile", "dockerfile")]
    [InlineData(".razor", "razor")]
    [InlineData(".unknown", "plaintext")]
    [InlineData(".scss", "scss")]
    [InlineData(".csproj", "xml")]
    public void InferLanguage_ReturnsExpected(string ext, string expected)
    {
        var result = GitRepositoryViewHelpers.InferLanguage($"file{ext}");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(PullRequestStatus.Open, OmniTone.Success)]
    [InlineData(PullRequestStatus.Merged, OmniTone.Accent)]
    [InlineData(PullRequestStatus.Closed, OmniTone.Danger)]
    [InlineData(PullRequestStatus.Draft, OmniTone.Neutral)]
    public void GetPrStatusBadge_ReturnsExpected(PullRequestStatus status, OmniTone expected)
    {
        var method = typeof(GitRepositoryDetail).GetMethod("GetPrStatusBadge",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = (OmniTone)method.Invoke(null, [status])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBlame_FormatsCorrectly()
    {
        var lines = new List<GitLightBlameLine>
        {
            new()
            {
                LineNumber = 1,
                Sha = "abc123456789",
                ShortSha = "abc1234",
                AuthorName = "Developer",
                AuthorDate = new DateTime(2026, 1, 15),
                Line = "using System;"
            }
        };
        var result = GitRepositoryViewHelpers.FormatBlame(lines);
        Assert.Contains("abc1234", result);
        Assert.Contains("Developer", result);
        Assert.Contains("using System;", result);
    }
}
