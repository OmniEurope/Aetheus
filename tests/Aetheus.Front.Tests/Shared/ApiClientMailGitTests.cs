// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Front.Tests;

/// <summary>Covers ApiClient.Mail and ApiClient.Git partials with exact HTTP contracts.</summary>
public class ApiClientMailGitTests
{
    private readonly BunitTestHelper.TestHandler _handler = new();
    private readonly ApiClient _api;

    public ApiClientMailGitTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://test/") };
        _api = new ApiClient(http);
    }

    [Fact]
    public async Task GetMailStateAsync_UsesExpectedRequest()
    {
        const string url = "api/servers/1/mail";
        _handler.SetJsonResponse(HttpMethod.Get, url, new MailDataDto { IsInstalled = true, PostfixVersion = "3.8" });

        var result = await _api.Mail.GetMailStateAsync(1, Xunit.TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.Equal("3.8", result.PostfixVersion);
        AssertRequest(HttpMethod.Get, url);
    }

    [Theory]
    [InlineData("domains")]
    [InlineData("accounts")]
    [InlineData("aliases")]
    public async Task GetMailCollectionsAsync_UsesExpectedPagination(string resource)
    {
        var url = $"api/servers/1/mail/{resource}?page=1&pageSize=100&sortDescending=false";
        if (resource == "domains")
        {
            _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<MailDomainDto>());
            Assert.Empty(await _api.Mail.GetMailDomainsAsync(1));
        }
        else if (resource == "accounts")
        {
            _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<MailAccountDto>());
            Assert.Empty(await _api.Mail.GetMailAccountsAsync(1));
        }
        else
        {
            _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<MailAliasDto>());
            Assert.Empty(await _api.Mail.GetMailAliasesAsync(1));
        }

        AssertRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task GetMailDomainAsync_UsesExpectedRequest()
    {
        const string url = "api/servers/1/mail/domains/1";
        _handler.SetJsonResponse(HttpMethod.Get, url, new MailDomainDto { Id = 1, Name = "example.com" });

        var result = await _api.Mail.GetMailDomainAsync(1, 1, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("example.com", result!.Name);
        AssertRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task CreateMailDomainAsync_UsesPost()
    {
        const string url = "api/servers/1/mail/domains";
        _handler.SetJsonResponse(HttpMethod.Post, url, new MailDomainDto { Id = 2, Name = "new.example.com" });

        var result = await _api.Mail.CreateMailDomainAsync(1, new CreateMailDomainRequest(), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("new.example.com", result!.Name);
        AssertRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task CreateMailAccountAsync_UsesPost()
    {
        const string url = "api/servers/1/mail/accounts";
        _handler.SetJsonResponse(HttpMethod.Post, url, new MailAccountDto { Id = 5, Email = "user@example.com" });

        var result = await _api.Mail.CreateMailAccountAsync(1, new CreateMailAccountRequest(), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("user@example.com", result!.Email);
        AssertRequest(HttpMethod.Post, url);
    }

    [Theory]
    [InlineData("domains/1")]
    [InlineData("accounts/1")]
    [InlineData("aliases/1")]
    public async Task DeleteMailResourceAsync_UsesDelete(string resource)
    {
        var url = $"api/servers/1/mail/{resource}";
        _handler.SetResponse(HttpMethod.Delete, url, HttpStatusCode.NoContent);

        var result = resource.Split('/')[0] switch
        {
            "domains" => await _api.Mail.DeleteMailDomainAsync(1, 1, Xunit.TestContext.Current.CancellationToken),
            "accounts" => await _api.Mail.DeleteMailAccountAsync(1, 1, Xunit.TestContext.Current.CancellationToken),
            _ => await _api.Mail.DeleteMailAliasAsync(1, 1, Xunit.TestContext.Current.CancellationToken)
        };

        Assert.True(result.Success);
        AssertRequest(HttpMethod.Delete, url);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("logs")]
    public async Task ExecuteMailCommandAsync_UsesPost(string command)
    {
        var url = $"api/servers/1/mail/{command}";
        _handler.SetResponse(HttpMethod.Post, url, HttpStatusCode.NoContent);

        var result = command switch
        {
            "action" => await _api.Mail.ExecuteMailActionAsync(1, new MailActionRequest(), Xunit.TestContext.Current.CancellationToken),
            _ => await _api.Mail.GetMailLogsAsync(1, new MailLogRequest(), Xunit.TestContext.Current.CancellationToken)
        };

        Assert.True(result.Success);
        AssertRequest(HttpMethod.Post, url);
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("purge")]
    public async Task SetupAndPurge_PostAndReturnTheQueuedTaskId(string command)
    {
        // The page follows the task the setup or the reinstall queued.
        var url = $"api/servers/1/mail/{command}";
        _handler.SetJsonResponse(HttpMethod.Post, url, new MailTaskQueuedDto { TaskId = 42 });

        var result = command == "setup"
            ? await _api.Mail.SetupMailAsync(1, new MailSetupRequest(), Xunit.TestContext.Current.CancellationToken)
            : await _api.Mail.PurgeMailStackAsync(1, new MailPurgeRequest { Confirmation = "example.com" }, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(42, result?.TaskId);
        AssertRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task GetMailDnsRecordsAsync_UsesExpectedRequest()
    {
        const string url = "api/servers/1/mail/domains/1/dns";
        _handler.SetJsonResponse(HttpMethod.Get, url, new MailDnsRecordsDto { Domain = "example.com", DkimSelector = "default" });

        var result = await _api.Mail.GetMailDnsRecordsAsync(1, 1, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("example.com", result!.Domain);
        Assert.Equal("default", result.DkimSelector);
        AssertRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task GetGitReposAsync_UsesExpectedPagination()
    {
        const string url = "api/git/repos?page=1&pageSize=100&projectId=42&sortDescending=False";
        _handler.SetJsonResponse(HttpMethod.Get, url, Page(new GitLightRepoDto { Id = 3, Name = "svc", Slug = "svc" }));

        var result = await _api.Git.GetGitReposAsync(42, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("svc", Assert.Single(result).Slug);
        AssertRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task GetGitRepoAsync_UsesExpectedRequest()
    {
        const string url = "api/git/repos/1";
        _handler.SetJsonResponse(HttpMethod.Get, url, new GitLightRepoDto { Id = 1, Name = "repo", Slug = "repo" });

        var result = await _api.Git.GetGitRepoAsync(1, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(1, result!.Id);
        Assert.Equal("repo", result.Name);
        AssertRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task CreateGitRepoAsync_UsesPost()
    {
        const string url = "api/git/repos";
        _handler.SetJsonResponse(HttpMethod.Post, url, new GitLightRepoDto { Id = 2, Slug = "new-repo" });

        var result = await _api.Git.CreateGitRepoAsync(new CreateGitLightRepoRequest(), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("new-repo", result!.Slug);
        AssertRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task DeleteGitRepoAsync_UsesDelete()
    {
        const string url = "api/git/repos/1";
        _handler.SetResponse(HttpMethod.Delete, url, HttpStatusCode.NoContent);

        var result = await _api.Git.DeleteGitRepoAsync(1, Xunit.TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        AssertRequest(HttpMethod.Delete, url);
    }

    [Theory]
    [InlineData("branches")]
    [InlineData("tags")]
    [InlineData("tree")]
    [InlineData("branch-protection")]
    public async Task GetGitCollectionsAsync_UsesExpectedPagination(string resource)
    {
        var url = $"api/git/repos/1/{resource}?page=1&pageSize=100&sortDescending=false";
        switch (resource)
        {
            case "branches":
                _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<GitLightBranchDto>());
                Assert.Empty(await _api.Git.GetGitBranchesAsync(1, Xunit.TestContext.Current.CancellationToken));
                break;
            case "tags":
                _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<GitLightTagDto>());
                Assert.Empty(await _api.Git.GetGitTagsAsync(1, Xunit.TestContext.Current.CancellationToken));
                break;
            case "tree":
                _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<GitLightTreeEntryDto>());
                Assert.Empty(await _api.Git.GetGitTreeAsync(1, ct: Xunit.TestContext.Current.CancellationToken));
                break;
            default:
                _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<BranchProtectionRuleDto>());
                Assert.Empty(await _api.Git.GetGitBranchProtectionRulesAsync(1, Xunit.TestContext.Current.CancellationToken));
                break;
        }

        AssertRequest(HttpMethod.Get, url);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("blame")]
    public async Task GetGitFileDataAsync_UsesEncodedQuery(string resource)
    {
        var url = $"api/git/repos/1/{resource}?ref=main&path=file.txt";
        if (resource == "blob")
        {
            _handler.SetJsonResponse(HttpMethod.Get, url, new GitLightBlobDto { Path = "file.txt", Content = "hello" });
            var result = await _api.Git.GetGitBlobAsync(1, "main", "file.txt", Xunit.TestContext.Current.CancellationToken);
            Assert.Equal("hello", result!.Content);
        }
        else
        {
            _handler.SetJsonResponse(HttpMethod.Get, url, Array.Empty<GitLightBlameLine>());
            Assert.Empty(await _api.Git.GetGitBlameAsync(1, "main", "file.txt"));
        }

        AssertRequest(HttpMethod.Get, url);
    }

    private void AssertRequest(HttpMethod method, string relativeUrl)
    {
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(method.Method, request.Method);
        Assert.Equal($"http://test/{relativeUrl}", request.Url);
    }

    private static PaginatedResult<T> Page<T>(params T[] items) => new()
    {
        Items = [.. items],
        TotalCount = items.Length,
        Page = 1,
        PageSize = 100
    };
}
