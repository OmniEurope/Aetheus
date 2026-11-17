// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-36: an <c>on_success:</c> follow-up that declares a required parameter with no default, which the launch that follows does
/// not supply (it gives the release as <c>candidateVersion</c> and nothing else), is told before any run on the readiness, naming
/// the upstream pipeline, the follow-up and the parameter. <c>deployServer</c> keeps its own finding.
/// </summary>
public sealed class PipelineChainedRequiredParameterReadinessTests
{
    private const int ProjectId = 21;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Candidate(bool namesARelease) => string.Join('\n',
        "name: oe1-candidate",
        "trigger: manual",
        "on_success:",
        "  - pipeline: oe1-deploy-prod",
        namesARelease ? "    release: latest" : "    # no release named",
        "stages:",
        "  - name: Build",
        "    steps:",
        "      - name: Build",
        "        shell: echo build");

    private static string Follower(string name, bool required = true, string? defaultValue = null) => string.Join('\n',
        "name: oe1-deploy-prod",
        "trigger: manual",
        "parameters:",
        $"  - name: {name}",
        "    type: string",
        $"    required: {(required ? "true" : "false")}",
        defaultValue is null ? "    # no default" : $"    default: \"{defaultValue}\"",
        "stages:",
        "  - name: Deploy",
        "    steps:",
        "      - name: Deploy",
        "        shell: echo deploy");

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IPipelineSetupServerRepository _servers = Substitute.For<IPipelineSetupServerRepository>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();

    public PipelineChainedRequiredParameterReadinessTests()
    {
        _pipelines.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([11]);
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(
            [new GitLightRepoDto { Id = 4, ProjectId = ProjectId, Name = "oe1", DefaultBranch = "main", IsEmpty = false }]);
        _servers.ListProjectServerNamesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(["vps1", "vps2"]);
    }

    private async Task<List<PipelineSetupReadinessCheckDto>> ChecksAsync(string candidateYaml, string followerYaml)
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([(181, "oe1-candidate", candidateYaml), (182, "oe1-deploy-prod", followerYaml)]);
        var sut = new PipelineSetupReadinessService(
            Substitute.For<IPipelineTemplateService>(), _pipelines, _git, Substitute.For<IGitLightRepository>(),
            Substitute.For<IPipelineTemplateResolver>(), Substitute.For<IPipelineRequirementsChecker>(),
            Substitute.For<IProjectConfigRepositoryService>(), ReadinessVariableResolver.Create(), _servers,
            NullLogger<PipelineSetupReadinessService>.Instance);
        var readiness = await sut.CheckProjectAsync(ProjectId, pipelineId: 181, organizationId: 3, Ct);
        return [.. readiness.Checks.Where(check => check.Kind == PipelineSetupReadinessKind.ChainedRequiredParameterMissing)];
    }

    [Fact]
    public async Task ARequiredParameterWithNoDefault_NothingSupplies_IsSaidWithUpstreamFollowUpAndParameter()
    {
        var check = Assert.Single(await ChecksAsync(Candidate(namesARelease: true), Follower("approvalTicket")));

        Assert.Equal(["oe1-candidate > oe1-deploy-prod > approvalTicket"], check.Items);
        // A warning: the upstream run itself is not at risk, only the follow-up is.
        Assert.Equal(PipelineSetupReadinessSeverity.Warning, check.Severity);
    }

    [Fact]
    public async Task ARequiredParameterThatCarriesADefault_IsNotSaid()
    {
        Assert.Empty(await ChecksAsync(Candidate(namesARelease: true), Follower("approvalTicket", defaultValue: "none")));
    }

    [Fact]
    public async Task AnOptionalParameter_IsNotSaid()
    {
        Assert.Empty(await ChecksAsync(Candidate(namesARelease: true), Follower("approvalTicket", required: false)));
    }

    [Fact]
    public async Task CandidateVersion_IsSuppliedByTheEdgeThatNamesARelease_ButNotByOneThatDoesNot()
    {
        Assert.Empty(await ChecksAsync(Candidate(namesARelease: true), Follower("candidateVersion")));

        var check = Assert.Single(await ChecksAsync(Candidate(namesARelease: false), Follower("candidateVersion")));
        Assert.Equal(["oe1-candidate > oe1-deploy-prod > candidateVersion"], check.Items);
    }

    [Fact]
    public async Task DeployServer_KeepsItsOwnFinding_AndIsNotRepeatedHere()
    {
        Assert.Empty(await ChecksAsync(Candidate(namesARelease: true), Follower("deployServer")));
    }
}
