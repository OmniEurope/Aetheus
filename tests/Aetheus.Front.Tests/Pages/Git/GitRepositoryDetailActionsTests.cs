// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Git;
using Aetheus.Front.Resources;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Git;

/// <summary>
/// Every destructive Git action behind this coordinator is gated by a confirmation. The property that
/// matters is the negative one: declining must reach no API at all. A coordinator that asks, ignores
/// the answer and deletes anyway is worse than one that never asked, because the user believes they
/// cancelled.
/// </summary>
public sealed class GitRepositoryDetailActionsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public GitRepositoryDetailActionsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/git/repos/1/branches", new PaginatedResult<GitLightBranchDto>
        {
            Items = [new GitLightBranchDto { Name = "main" }],
            TotalCount = 1
        });
    }

    private GitRepositoryDetailActions Build() => new(
        Services.GetRequiredService<ApiClient>(),
        _dialog,
        Services.GetRequiredService<NotifyHelper>(),
        Services.GetRequiredService<IStringLocalizer<AppStrings>>());

    private static GitLightBranchDto Branch(string name = "feature/x") => new() { Name = name };

    private static GitLightTagDto Tag(string name = "v1.0.0") => new() { Name = name };

    private static InternalPullRequestDto Pr(int number = 42) => new() { Number = number };

    // --- declining a confirmation must never reach the API ------------------

    [Fact]
    public async Task DecliningABranchDeletion_CallsNoApi()
    {
        _dialog.ConfirmResult = false;

        var deleted = await Build().DeleteBranchAsync(1, Branch());

        Assert.False(deleted);
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task DecliningATagDeletion_CallsNoApi()
    {
        _dialog.ConfirmResult = false;

        var deleted = await Build().DeleteTagAsync(1, Tag());

        Assert.False(deleted);
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task DecliningAMerge_CallsNoApi()
    {
        // Merging is not reversible from this screen; an ignored cancel would land a PR silently.
        _dialog.ConfirmResult = false;

        var merged = await Build().MergePullRequestAsync(1, Pr());

        Assert.False(merged);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DecliningAPullRequestClose_CallsNoApi()
    {
        _dialog.ConfirmResult = false;

        var closed = await Build().ClosePullRequestAsync(1, Pr());

        Assert.False(closed);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("close", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ADismissedConfirmation_CountsAsDeclined_NotAsApproval()
    {
        // The dialog service returns null when the dialog is dismissed rather than answered. Treating null as
        // anything but "no" would delete on an Escape key press.
        _dialog.ConfirmResult = null;

        var deleted = await Build().DeleteBranchAsync(1, Branch());

        Assert.False(deleted);
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "DELETE");
    }

    // --- the configuration repository designation (ADR-057) -----------------

    [Fact]
    public async Task DesignatingTheConfigurationRepository_SendsTheFlagToTheRepository_AndReportsSuccess()
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/git/repos/5", new GitLightRepoDto { Id = 5, IsConfiguration = true });

        var done = await Build().SetConfigurationAsync(5, designate: true);

        Assert.True(done);
        var put = Assert.Single(_handler.RequestDetails, r => r.Method == "PUT" && r.Url.Contains("api/git/repos/5"));
        Assert.Contains("\"isConfiguration\":true", put.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GivingUpTheConfigurationRole_SendsFalse()
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/git/repos/5", new GitLightRepoDto { Id = 5, IsConfiguration = false });

        var done = await Build().SetConfigurationAsync(5, designate: false);

        Assert.True(done);
        var put = Assert.Single(_handler.RequestDetails, r => r.Method == "PUT");
        Assert.Contains("\"isConfiguration\":false", put.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARefusedDesignation_IsNotReportedAsDone()
    {
        // The server refuses a mirror and a project's last source repository: the page must not claim a change.
        _handler.SetResponse(HttpMethod.Put, "api/git/repos/5", HttpStatusCode.BadRequest);

        var done = await Build().SetConfigurationAsync(5, designate: true);

        Assert.False(done);
    }

    // --- accepting performs the call ---------------------------------------

    [Fact]
    public async Task AcceptingABranchDeletion_CallsTheApiAndReportsSuccess()
    {
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, "api/git/repos/1/branches", HttpStatusCode.NoContent);

        var deleted = await Build().DeleteBranchAsync(1, Branch());

        Assert.True(deleted);
        Assert.Contains(_handler.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task AFailedDeletion_IsReportedAsFailure_NotSilentlySwallowed()
    {
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, "api/git/repos/1/branches", HttpStatusCode.Conflict);

        Assert.False(await Build().DeleteBranchAsync(1, Branch()));
    }

    // NOTE: the harness localizer returns the resource KEY and ignores format arguments, so "the
    // message names the branch" is not observable here. What IS observable, and is the property that
    // matters, is that a confirmation was actually requested before anything was deleted.

    [Fact]
    public async Task ABranchIsNeverDeletedWithoutAskingFirst()
    {
        _dialog.ConfirmResult = false;

        await Build().DeleteBranchAsync(1, Branch("release/2.0"));

        Assert.NotNull(_dialog.LastConfirmMessage);
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task APullRequestIsNeverMergedWithoutAskingFirst()
    {
        _dialog.ConfirmResult = false;

        await Build().MergePullRequestAsync(1, Pr(77));

        Assert.NotNull(_dialog.LastConfirmMessage);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    // --- creation dialogs ---------------------------------------------------

    [Fact]
    public async Task CreatingABranch_OpensTheBranchDialogPreloadedWithTheExistingBranches()
    {
        // The dialog offers a start point; opening it without the branch list gives an empty picker.
        _dialog.OpenResult = true;

        var created = await Build().CreateBranchAsync(1);

        Assert.True(created);
        Assert.Equal(typeof(GitBranchCreateDialog), _dialog.LastComponent);
        Assert.Equal(1, _dialog.LastParameters!["RepoId"]);
        // Non-empty, not merely non-null: a regression making GetGitBranchesAsync a no-op would still
        // hand the dialog an empty picker.
        Assert.NotEmpty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(_dialog.LastParameters["Branches"]!).Cast<object>());
    }

    [Fact]
    public async Task CancellingTheCreateDialog_ReportsNoChange()
    {
        _dialog.OpenResult = null;

        Assert.False(await Build().CreateBranchAsync(1));
    }

    [Fact]
    public async Task CreatingATag_OpensTheTagDialog_NotTheBranchOne()
    {
        _dialog.OpenResult = true;

        await Build().CreateTagAsync(1);

        Assert.Equal(typeof(GitTagCreateDialog), _dialog.LastComponent);
    }

    [Fact]
    public async Task CreatingAPullRequest_OpensThePrDialogWithTheBranchList()
    {
        _dialog.OpenResult = true;

        var created = await Build().CreatePullRequestAsync(1);

        Assert.True(created);
        Assert.Equal(typeof(GitPrCreateDialog), _dialog.LastComponent);
        Assert.NotEmpty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(_dialog.LastParameters!["Branches"]!).Cast<object>());
    }

    [Fact]
    public async Task AddingAProtectionRule_OpensTheProtectionDialogForTheRepository()
    {
        _dialog.OpenResult = true;

        var added = await Build().AddProtectionRuleAsync(1);

        Assert.True(added);
        Assert.Equal(typeof(GitBranchProtectionDialog), _dialog.LastComponent);
        Assert.Equal(1, _dialog.LastParameters!["RepoId"]);
    }

    [Fact]
    public async Task D13_ChangingTheProtectionLevel_PutsTheLevel_AndKeepsTheOtherSwitches()
    {
        _handler.SetJsonResponse("api/git/repos/1/branch-protection/5",
            new BranchProtectionRuleDto { Id = 5, Pattern = "main", ProtectionLevel = BranchProtectionLevel.StrictlyProtected });
        var rule = new BranchProtectionRuleDto
        {
            Id = 5,
            Pattern = "main",
            PreventDeletion = true,
            PreventForcePush = false,
            ProtectionLevel = BranchProtectionLevel.ProtectedExceptPipelines
        };

        var updated = await Build().UpdateProtectionRuleAsync(
            1, rule, preventDeletion: null, preventForcePush: null, protectionLevel: BranchProtectionLevel.StrictlyProtected);

        Assert.True(updated);
        var put = Assert.Single(_handler.RequestDetails, r => r.Method == "PUT");
        var sent = System.Text.Json.JsonSerializer.Deserialize<UpdateBranchProtectionRuleRequest>(put.Body!, BodyJson)!;
        Assert.Equal(BranchProtectionLevel.StrictlyProtected, sent.ProtectionLevel);
        Assert.True(sent.PreventDeletion);
        Assert.False(sent.PreventForcePush);
    }

    private static readonly System.Text.Json.JsonSerializerOptions BodyJson =
        new(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}
