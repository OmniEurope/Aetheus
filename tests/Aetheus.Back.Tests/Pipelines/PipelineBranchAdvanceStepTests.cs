// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R2-001: <c>type: advance-branch</c> moves a branch onto the commit of the release the run
/// deploys, as its candidate recorded it, and every outcome is visible on the step.
/// </summary>
public class PipelineBranchAdvanceStepTests
{
    private const int RunId = 42;
    private const int ProjectId = 3;
    private const string Version = "2.4.0-2483";
    private const string CandidateSha = "1111111111111111111111111111111111111111";
    private const string PreviousSha = "0000000000000000000000000000000000000000";

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IGitBranchAdvanceService _advance = Substitute.For<IGitBranchAdvanceService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly PipelineBranchAdvanceStep _sut;
    private readonly PipelineStepRun _stepRun = new() { Id = 7, PipelineRunId = RunId, StepName = "Advance main" };
    private readonly List<string> _runLines = [];

    public PipelineBranchAdvanceStepTests()
    {
        _sut = new PipelineBranchAdvanceStep(_pipelines, _advance, _audit, new FakeTimeProvider());
        var pipeline = new Pipeline { Id = 1, Name = "aetheus-deploy-prod", ProjectId = ProjectId };
        _pipelines.GetPipelineRunWithPipelineAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = RunId, Pipeline = pipeline, CommitHash = "2222222222222222222222222222222222222222" });
        _pipelines.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(ProjectId);
        _pipelines.AppendRunWarningsAsync(RunId, Arg.Do<IReadOnlyCollection<string>>(lines => _runLines.AddRange(lines)),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        GivenRelease(ReleaseStatus.Deployed, CandidateSha);
    }

    private void GivenRelease(ReleaseStatus status, string? commit) =>
        _pipelines.FindBranchAdvanceReleaseAsync(ProjectId, Version, Arg.Any<CancellationToken>())
            .Returns(new BranchAdvanceRelease(9, Version, status, commit));

    private void GivenOutcome(GitBranchAdvanceOutcome outcome, string? previous = PreviousSha, string? detail = null) =>
        _advance.TryFastForwardAsync(ProjectId, "main", CandidateSha, Arg.Any<CancellationToken>())
            .Returns(new GitBranchAdvanceResult(outcome, "aetheus", previous, detail));

    private Task ExecuteAsync(
        string? branch = "main", string? candidateVersion = Version, string? tag = null, string? appVersion = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (candidateVersion is not null) variables["AETHEUS_CANDIDATE_VERSION"] = candidateVersion;
        if (appVersion is not null) variables["APP_VERSION"] = appVersion;
        return _sut.ExecuteAsync(
            RunId, _stepRun, new PipelineStepDefinition { Name = "Advance main", Type = "advance-branch", Branch = branch, Tag = tag },
            variables, TestContext.Current.CancellationToken);
    }

    private void GivenTag(GitTagOutcome outcome, string? existing = null) =>
        _advance.TryCreateTagAsync(ProjectId, "v1.3.15", CandidateSha, Arg.Any<CancellationToken>())
            .Returns(new GitTagResult(outcome, "aetheus", existing));

    [Fact]
    public async Task ReleaseTag_NamedAfterTheDeployedVersion_GoesOnTheReleaseCommit_AndIsAudited()
    {
        GivenOutcome(GitBranchAdvanceOutcome.Advanced);
        GivenTag(GitTagOutcome.Created);

        await ExecuteAsync(tag: "v$(APP_VERSION)", appVersion: "1.3.15");

        await _advance.Received(1).TryCreateTagAsync(ProjectId, "v1.3.15", CandidateSha, Arg.Any<CancellationToken>());
        Assert.Equal(TaskExecutionStatus.Success, _stepRun.Status);
        Assert.Empty(_runLines);
        Assert.Contains("'main' advanced", Outcome(), StringComparison.Ordinal);
        Assert.Contains("Tag 'v1.3.15' created on 111111111111", Outcome(), StringComparison.Ordinal);
        await _audit.Received(1).LogAsync("TaggedReleaseAfterDeploy", "Release", 9,
            Arg.Is<string>(details => details.Contains(CandidateSha, StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseTag_IsCreated_EvenWhenTheBranchWasAlreadyPastTheRelease()
    {
        GivenOutcome(GitBranchAdvanceOutcome.AlreadyContains, "2222222222222222222222222222222222222222");
        GivenTag(GitTagOutcome.AlreadyThere);

        await ExecuteAsync(tag: "v$(APP_VERSION)", appVersion: "1.3.15");

        Assert.Equal(TaskExecutionStatus.Success, _stepRun.Status);
        Assert.Contains("Tag 'v1.3.15' already on 111111111111", Outcome(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseTag_OnAnotherCommit_FailsTheStep_WithBothCommits()
    {
        GivenOutcome(GitBranchAdvanceOutcome.Advanced);
        GivenTag(GitTagOutcome.OnAnotherCommit, "3333333333333333333333333333333333333333");

        await ExecuteAsync(tag: "v$(APP_VERSION)", appVersion: "1.3.15");

        Assert.Equal(TaskExecutionStatus.Failed, _stepRun.Status);
        Assert.Contains("333333333333", _stepRun.FailureReason, StringComparison.Ordinal);
        Assert.Contains("111111111111", _stepRun.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseTag_FromAVariableTheRunDoesNotSet_FailsTheStep_WithoutWritingATag()
    {
        GivenOutcome(GitBranchAdvanceOutcome.Advanced);

        await ExecuteAsync(tag: "v$(APP_VERSION)");

        Assert.Equal(TaskExecutionStatus.Failed, _stepRun.Status);
        Assert.Contains("v$(APP_VERSION)", _stepRun.FailureReason, StringComparison.Ordinal);
        await _advance.DidNotReceiveWithAnyArgs().TryCreateTagAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NoTag_CreatesNoTag()
    {
        GivenOutcome(GitBranchAdvanceOutcome.Advanced);

        await ExecuteAsync(appVersion: "1.3.15");

        Assert.Equal(TaskExecutionStatus.Success, _stepRun.Status);
        await _advance.DidNotReceiveWithAnyArgs().TryCreateTagAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Advances_TheBranch_OntoTheCommitTheCandidateRecorded_AndSaysSo()
    {
        GivenOutcome(GitBranchAdvanceOutcome.Advanced);

        await ExecuteAsync();

        // The release's commit, never the run's own workspace head (2222...).
        await _advance.Received(1).TryFastForwardAsync(ProjectId, "main", CandidateSha, Arg.Any<CancellationToken>());
        Assert.Equal(TaskExecutionStatus.Success, _stepRun.Status);
        Assert.Equal(0, _stepRun.ExitCode);
        Assert.NotNull(_stepRun.CompletedAt);
        // R2-071: a success is no run warning; it is the step's output.
        Assert.Empty(_runLines);
        var line = Outcome();
        Assert.Contains("'main' advanced from 000000000000 to 111111111111", line, StringComparison.Ordinal);
        Assert.Contains(Version, line, StringComparison.Ordinal);
        await _audit.Received(1).LogAsync("AdvancedBranchAfterDeploy", "Release", 9,
            Arg.Is<string>(details => details.Contains(CandidateSha, StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyThere_Succeeds_WithALineSayingNothingMoved()
    {
        GivenOutcome(GitBranchAdvanceOutcome.AlreadyUpToDate, CandidateSha);

        await ExecuteAsync();

        Assert.Equal(TaskExecutionStatus.Success, _stepRun.Status);
        Assert.Empty(_runLines);
        Assert.Contains("already points at 111111111111", Outcome(), StringComparison.Ordinal);
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2105_ABranchAlreadyPastTheRelease_Succeeds_WithAnInfoLine_AndNoWarning()
    {
        // Recette R2-105 (aetheus-deploy-prod #2543): 'main' had been pushed to 011a2e9f4069, past the
        // deployed candidate 9510ba4abd1e, and the whole successful deployment was marked failed.
        GivenOutcome(GitBranchAdvanceOutcome.AlreadyContains, "2222222222222222222222222222222222222222");

        await ExecuteAsync();

        Assert.Equal(TaskExecutionStatus.Success, _stepRun.Status);
        Assert.Equal(0, _stepRun.ExitCode);
        Assert.Null(_stepRun.FailureReason);
        Assert.Empty(_runLines);
        Assert.Contains("'main' is at 222222222222, which already contains 111111111111", Outcome(), StringComparison.Ordinal);
        Assert.Contains("nothing to advance", Outcome(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R2105_ADivergedBranch_IsARunWarning_NeverAFailedStep()
    {
        GivenOutcome(GitBranchAdvanceOutcome.NotFastForward);

        await ExecuteAsync();

        Assert.Equal(TaskExecutionStatus.Success, _stepRun.Status);
        Assert.Null(_stepRun.FailureCode);
        Assert.Null(_stepRun.FailureReason);
        Assert.Contains("has diverged from 111111111111", Outcome(), StringComparison.Ordinal);
        Assert.Contains("the branch was left where it is", Assert.Single(_runLines), StringComparison.Ordinal);
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(GitBranchAdvanceOutcome.CommitNotFound, "no internal repository of the project holds commit")]
    [InlineData(GitBranchAdvanceOutcome.Refused, "was refused: cannot lock ref")]
    public async Task CommitNotFound_OrRefused_FailsTheStep(GitBranchAdvanceOutcome outcome, string expected)
    {
        GivenOutcome(outcome, detail: "cannot lock ref");

        await ExecuteAsync();

        Assert.Equal(TaskExecutionStatus.Failed, _stepRun.Status);
        Assert.Contains(expected, _stepRun.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D13_AStrictlyProtectedBranch_FailsTheStep_WithTheReason_AndNoTag()
    {
        GivenOutcome(GitBranchAdvanceOutcome.Protected,
            detail: "Branch 'main' is strictly protected: only a merged pull request moves it, pipelines included.");

        await ExecuteAsync(tag: "v$(APP_VERSION)", appVersion: "1.3.15");

        Assert.Equal(TaskExecutionStatus.Failed, _stepRun.Status);
        Assert.Contains("'main' was not advanced to 111111111111", _stepRun.FailureReason, StringComparison.Ordinal);
        Assert.Contains("strictly protected", _stepRun.FailureReason, StringComparison.Ordinal);
        Assert.Contains("Protected except pipelines", _stepRun.FailureReason, StringComparison.Ordinal);
        await _advance.DidNotReceiveWithAnyArgs().TryCreateTagAsync(default, default!, default!, TestContext.Current.CancellationToken);
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AMissingRelease_FailsTheStep_WithoutTouchingGit()
    {
        _pipelines.FindBranchAdvanceReleaseAsync(ProjectId, Version, Arg.Any<CancellationToken>()).Returns((BranchAdvanceRelease?)null);

        await ExecuteAsync();

        Assert.Equal(TaskExecutionStatus.Failed, _stepRun.Status);
        Assert.Equal($"release '{Version}' does not exist in this project.", _stepRun.FailureReason);
        await _advance.DidNotReceiveWithAnyArgs().TryFastForwardAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AReleaseNotYetRecordedAsDeployed_FailsTheStep()
    {
        GivenRelease(ReleaseStatus.Published, CandidateSha);

        await ExecuteAsync();

        Assert.Equal(TaskExecutionStatus.Failed, _stepRun.Status);
        Assert.Contains("not Deployed", _stepRun.FailureReason, StringComparison.Ordinal);
        await _advance.DidNotReceiveWithAnyArgs().TryFastForwardAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARunWithoutACandidateVersion_FailsTheStep()
    {
        await ExecuteAsync(candidateVersion: null);

        Assert.Equal(TaskExecutionStatus.Failed, _stepRun.Status);
        Assert.Contains("AETHEUS_CANDIDATE_VERSION", _stepRun.FailureReason, StringComparison.Ordinal);
        await _advance.DidNotReceiveWithAnyArgs().TryFastForwardAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AMissingBranch_FailsTheStep()
    {
        await ExecuteAsync(branch: "  ");

        Assert.Equal(TaskExecutionStatus.Failed, _stepRun.Status);
        Assert.Equal("missing 'branch' to advance.", _stepRun.FailureReason);
    }

    private string Outcome() =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(_stepRun.OutputVariablesJson!)![PipelineBranchAdvanceStep.OutcomeOutput];
}
