// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-48, the production incident of 2026-10-25: the candidate succeeded, its <c>on_success</c>
/// target <c>aetheus-deploy-prod</c> carried a description with an unquoted " : " (not YAML), the
/// follow-up silently did not start and nothing said why. This walks the real handler, the real
/// trigger-step coordinator, the real run preparation and the real YamlDotNet parser, so the reason
/// written on the upstream run is the parser's own. The persistence, the Git reads, the launcher and the
/// notification store are doubles: this proves the reasoning and the messages, not a run on a server.
/// </summary>
public sealed class PipelineOnSuccessInvalidTargetYamlTests
{
    private const string Url = "https://git.example/aetheus.git";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string BrokenTargetYaml =
        "name: aetheus-deploy-prod\ntrigger: manual\nparameters:\n  - name: scope\n    description: Diagnostic : liste\nstages: []\n";
    private const string UpstreamYaml = "name: aetheus-candidate\non_success:\n  - pipeline: aetheus-deploy-prod\n";

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IUserNotificationService _userNotifications = Substitute.For<IUserNotificationService>();
    private readonly IPipelineChildRunLauncher _launcher = Substitute.For<IPipelineChildRunLauncher>();
    private readonly PipelineRunCompletedDownstreamHandler _handler;

    public PipelineOnSuccessInvalidTargetYamlTests()
    {
        var gitCli = Substitute.For<IGitCliService>();
        var target = new Pipeline
        {
            Id = 3,
            ProjectId = 7,
            Project = new Project { Id = 7, RepositoryUrl = Url, DefaultBranch = "main" },
            Name = "aetheus-deploy-prod",
            CreatedByUsername = "admin",
            YamlDefinition = "name: aetheus-deploy-prod\nstages: []\n"
        };
        gitCli.ResolveBranchCommitAsync(Url, "main", Arg.Any<CancellationToken>()).Returns(new GitRemoteBranch("main", Commit));
        gitCli.ReadPipelineYamlAsync(Url, Commit, target.Name, Arg.Any<CancellationToken>()).Returns(BrokenTargetYaml);
        _repo.FindPipelineAsync(3, Arg.Any<CancellationToken>()).Returns(target);
        _repo.FindPipelineByNameAndProjectAsync("aetheus-deploy-prod", 7, Arg.Any<CancellationToken>()).Returns(target);
        _repo.GetPipelineOrganizationIdAsync(3, Arg.Any<CancellationToken>()).Returns(1);

        var preparations = new PipelineRunPreparationService(
            _repo, Substitute.For<IPipelineGitService>(), new PipelineTemplateResolver(_repo),
            Substitute.For<IPipelineRunParameterResolver>(), gitCli, Substitute.For<IPipelineWorkspaceSourceResolver>(),
            new ConfigurationBuilder().Build(), NullLogger<PipelineRunPreparationService>.Instance);
        var coordinator = new PipelineTriggerStepCoordinator(
            _repo, Substitute.For<IPipelineGitService>(), preparations, Substitute.For<IPipelineRunParameterResolver>(),
            Substitute.For<IPipelineCheckpointReuseService>(), Substitute.For<IResourceAuthorizationService>(),
            Substitute.For<IAuditService>(), new FakeTimeProvider(), NullLogger<PipelineTriggerStepCoordinator>.Instance);
        var runService = Substitute.For<IPipelineRunService>();
        runService.TriggerChainedRunAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call => coordinator.TriggerChainedRunAsync(
                call.ArgAt<int>(0), call.ArgAt<Dictionary<string, string>>(1), _launcher, call.ArgAt<CancellationToken>(2)));
        _handler = new PipelineRunCompletedDownstreamHandler(
            _repo, runService, new PipelineRunNotificationPublisher(_repo, _userNotifications),
            NullLogger<PipelineRunCompletedDownstreamHandler>.Instance);

        var upstream = new Pipeline { Id = 24, Name = "aetheus-candidate", YamlDefinition = UpstreamYaml };
        var run = new PipelineRun { Id = 99, PipelineId = 24, YamlSnapshot = UpstreamYaml, Pipeline = upstream };
        _repo.GetPipelineRunWithPipelineAsync(99, Arg.Any<CancellationToken>()).Returns(run);
        _repo.GetPipelineProjectIdAsync(upstream, Arg.Any<CancellationToken>()).Returns(7);
    }

    [Fact]
    public async Task ASucceededCandidate_WhoseTargetYamlIsNotYaml_RecordsTheParserReasonOnItsRun_AndNotifies()
    {
        var exception = await Record.ExceptionAsync(() => _handler.HandleAsync(
            new PipelineRunCompletedEvent(99, PipelineStatus.Success), TestContext.Current.CancellationToken));

        Assert.Null(exception);
        await _repo.Received(1).AppendRunWarningsAsync(99,
            Arg.Is<IReadOnlyCollection<string>>(warnings => StoredProblem.English(warnings.Single()).StartsWith(
                    "The on_success follow-up 'aetheus-deploy-prod' was not started: its definition cannot be prepared:", StringComparison.Ordinal)
                && StoredProblem.CodeOf(warnings.Single()) == "OnSuccessFollowUpNotStarted"
                && StoredProblem.ParameterOf(warnings.Single(), "target") == "aetheus-deploy-prod"
                && StoredProblem.English(warnings.Single()).Contains("While scanning a plain scalar value, found invalid mapping")
                && StoredProblem.English(warnings.Single()).Contains("line 5, column")
                && !warnings.Single().Contains("authority")),
            Arg.Any<CancellationToken>());
        await _userNotifications.Received(1).RecordProjectEventAsync(
            "pipeline.launch-refused",
            Arg.Is<string>(payload => payload.Contains("\"PipelineRunId\":99")
                && payload.Contains("\"PipelineName\":\"aetheus-deploy-prod\"")
                && payload.Contains("on_success:aetheus-candidate#99")
                && payload.Contains("found invalid mapping")),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>());
        await _launcher.DidNotReceiveWithAnyArgs().TriggerPreparedRunAsync(default!, default, default, TestContext.Current.CancellationToken, default);
    }
}
