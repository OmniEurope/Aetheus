// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// R-34: a deployment run delivers the candidate release it restores, not the head of the branch it checked out.
/// The run names that release and its commit (the source head stays, labelled as such), the grid lists the release's
/// commit, the approval warns when the release is older than the head or already in production and waits for the
/// approver's confirmation, and a backend-run step shows its one line as its log.
/// </summary>
public sealed class PipelineRunDeployedReleaseTests : BunitContext
{
    private const string CandidateCommit = "7abbb42eff9ab68b138774ae556dbf39d7b7d089";
    private const string HeadCommit = "e8c21992aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public PipelineRunDeployedReleaseTests()
    {
        BunitTestHelper.RegisterServices(this, isAdmin: true);
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
    }

    private static PipelineRunDeployedReleaseDto Release(bool behind = false, bool alreadyDeployed = false) => new()
    {
        ReleaseId = 9,
        Version = "c-7abbb42",
        CommitHash = CandidateCommit,
        Status = ReleaseStatus.Published,
        SourceBranch = "develop",
        SourceCommitHash = HeadCommit,
        IsBehindSourceHead = behind,
        IsAlreadyDeployed = alreadyDeployed
    };

    private PipelineRunApprovalController Controller(PipelineRunDeployedReleaseDto? release)
    {
        var controller = new PipelineRunApprovalController(
            Services.GetRequiredService<ApiClient>(), Services.GetRequiredService<NotifyHelper>(), () => Task.CompletedTask);
        controller.Show(new PipelineApprovalDto
        {
            Id = 31,
            PipelineRunId = 40,
            StageName = "Approve",
            Scope = ApprovalScope.Pipeline,
            Status = ApprovalStatus.Pending,
            DeployedRelease = release
        });
        return controller;
    }

    private IRenderedComponent<PipelineRunApprovalPanel> RenderPanel(PipelineRunApprovalController controller) =>
        Render<PipelineRunApprovalPanel>(parameters => parameters.Add(component => component.Approval, controller));

    [Fact]
    public void AReleaseOlderThanTheSourceHead_IsWarned_AndApproveWaitsForTheConfirmation()
    {
        var controller = Controller(Release(behind: true));

        var cut = RenderPanel(controller);

        Assert.Contains("ApprovalDeploysRelease", cut.Find("[data-testid=approval-deployed-release]").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid=approval-warning-behind-head]"));
        Assert.Empty(cut.FindAll("[data-testid=approval-warning-already-deployed]"));
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
        Assert.Null(cut.Find("button.approval-decision.omni-button--primary").GetAttribute("disabled"));
    }

    [Fact]
    public void AReleaseAlreadyInProduction_IsWarned()
    {
        var cut = RenderPanel(Controller(Release(alreadyDeployed: true)));

        Assert.Single(cut.FindAll("[data-testid=approval-warning-already-deployed]"));
        Assert.Empty(cut.FindAll("[data-testid=approval-warning-behind-head]"));
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void ACurrentRelease_IsNamedWithoutWarning_AndApproveIsFree()
    {
        var controller = Controller(Release());

        var cut = RenderPanel(controller);

        Assert.Single(cut.FindAll("[data-testid=approval-deployed-release]"));
        Assert.Empty(cut.FindAll(".approval-release-warning-list"));
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void AnApprovalWithoutARelease_ShowsNoReleaseBlock()
    {
        var controller = Controller(null);

        var cut = RenderPanel(controller);

        Assert.Empty(cut.FindAll("[data-testid=approval-deployed-release]"));
    }

    [Fact]
    public void TheRunPage_NamesTheDeployedRelease_AndDropsTheSourceHeadLine()
    {
        var run = new PipelineRunDto
        {
            Id = 3167,
            PipelineId = 1,
            ProjectId = 4,
            ProjectName = "Wordmorrow",
            BranchName = "develop",
            CommitHash = HeadCommit,
            DeployedRelease = Release(behind: true)
        };

        var cut = Render<PipelineRunSourceTile>(parameters => parameters.Add(component => component.Run, run));

        // Terms are localizer keys under test; each row is its term then its value, in this order.
        var text = cut.Find(".run-source-tile").TextContent;
        var release = text.IndexOf("DeployedRelease", StringComparison.Ordinal);
        var commit = text.IndexOf("DeployedCommit", StringComparison.Ordinal);
        Assert.InRange(commit, release + 1, int.MaxValue);
        // R-36: the source branch head is no longer a line of the tile (neither labelled nor as a plain commit).
        Assert.DoesNotContain("SourceHeadCommit", text, StringComparison.Ordinal);
        Assert.DoesNotContain("e8c21992", text, StringComparison.Ordinal);
        Assert.Contains("c-7abbb42", text[release..commit], StringComparison.Ordinal);
        Assert.Contains("7abbb42e", text[commit..], StringComparison.Ordinal);
        Assert.Contains("/releases/9", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeployedRelease_IsShownShort_WithTheFullNumberOnHover()
    {
        const string longVersion = "c-4796537abcdef0123456789abcdef0123456789a";
        var run = new PipelineRunDto
        {
            Id = 3167,
            PipelineId = 1,
            ProjectId = 4,
            CommitHash = HeadCommit,
            DeployedRelease = Release() with { Version = longVersion }
        };

        var cut = Render<PipelineRunSourceTile>(parameters => parameters.Add(component => component.Run, run));

        var row = cut.Find(".run-deployed-release .release-version");
        Assert.Equal("c-4796537a", row.TextContent.Trim());
        Assert.Equal(longVersion, row.GetAttribute("title"));
    }

    [Fact]
    public void AnOrdinaryRun_KeepsTheCommitRow_AndShowsNoDeployedRows()
    {
        var run = new PipelineRunDto { Id = 1, PipelineId = 1, ProjectId = 4, BranchName = "develop", CommitHash = HeadCommit };

        var cut = Render<PipelineRunSourceTile>(parameters => parameters.Add(component => component.Run, run));

        Assert.DoesNotContain("SourceHeadCommit", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("DeployedRelease", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("e8c21992", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunsGridRow_ListsTheDeployedCommit_AndKeepsTheSourceHeadApart()
    {
        var item = PipelineRunTableItem.FromRun(new PipelineRunDto
        {
            Id = 3167,
            CommitHash = HeadCommit,
            RepositoryId = 7,
            Commits = [new CommitLinkDto { Id = 5, Sha = HeadCommit }],
            DeployedRelease = Release(behind: true)
        });

        Assert.Equal(CandidateCommit, item.CommitHash);
        Assert.Equal(HeadCommit, item.SourceCommitHash);
        Assert.Equal("c-7abbb42", item.DeployedVersion);
        // The git-graph page is the source head's: the deployed commit is linked through the repository.
        Assert.Null(item.CommitId);
        Assert.Equal($"/git-repositories/7/commits/{CandidateCommit}", PipelineRunTablePresentation.CommitHref(item));
    }

    [Fact]
    public void ARunThatDeploysNoRelease_ListsItsOwnCommit()
    {
        var item = PipelineRunTableItem.FromRun(new PipelineRunDto { Id = 1, CommitHash = HeadCommit });

        Assert.Equal(HeadCommit, item.CommitHash);
        Assert.Null(item.SourceCommitHash);
        Assert.Null(item.DeployedVersion);
    }

    [Fact]
    public void ABackendRunStep_ShowsItsAccount_AsItsLog_NeverAsLogsNotRetained()
    {
        var step = new PipelineStepRunDto
        {
            Id = 1,
            StepName = "Advance main",
            StageName = "Advance main",
            Status = TaskExecutionStatus.Success,
            OutputVariables = new Dictionary<string, string>
            {
                [PipelineStepOutputs.BranchAdvanceOutcome] = "Nothing was advanced: 'main' already points at 7abbb42."
            }
        };

        Assert.Equal("Nothing was advanced: 'main' already points at 7abbb42.", PipelineRunLogView.BackendStepLine(step));
    }

    [Fact]
    public void AStepWithATask_OrNoAccount_HasNoBackendLine()
    {
        var withTask = new PipelineStepRunDto
        {
            Id = 1,
            TaskId = 12,
            OutputVariables = new Dictionary<string, string> { [PipelineStepOutputs.BranchAdvanceOutcome] = "x" }
        };
        var withoutAccount = new PipelineStepRunDto { Id = 2 };

        Assert.Null(PipelineRunLogView.BackendStepLine(withTask));
        Assert.Null(PipelineRunLogView.BackendStepLine(withoutAccount));
    }
}
