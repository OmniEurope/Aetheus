// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Project copies adapt names and references while copying only library and vault keys,
/// with rollback on failure.</summary>
public sealed class PipelineProjectCopyServiceTests
{
    private const int SourceProjectId = 1;
    private const int TargetProjectId = 2;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IPipelineService _pipelineService = Substitute.For<IPipelineService>();
    private readonly IPipelineSetupReadinessService _readiness = Substitute.For<IPipelineSetupReadinessService>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();
    private readonly PipelineProjectCopyService _sut;
    private int _nextPipelineId = 500;

    private const string CandidateYaml = """
        name: aetheus-candidate
        trigger: manual
        variable_libraries:
          - aetheus-prod-host
        vaults:
          - aetheus-deploy
        on_success:
          - pipeline: aetheus-deploy-prod
        stages: []
        """;

    private const string DeployYaml = """
        name: aetheus-deploy-prod
        trigger: manual
        stages: []
        """;

    public PipelineProjectCopyServiceTests()
    {
        _sut = new PipelineProjectCopyService(_pipelines, _pipelineService, _readiness, _libraries, _vaults,
            NullLogger<PipelineProjectCopyService>.Instance);
        _pipelines.GetPipelineDefinitionsForProjectAsync(SourceProjectId, Arg.Any<CancellationToken>())
            .Returns([(10, "aetheus-candidate", CandidateYaml), (11, "aetheus-deploy-prod", DeployYaml)]);
        _pipelines.GetPipelineDefinitionsForProjectAsync(TargetProjectId, Arg.Any<CancellationToken>())
            .Returns(new List<(int, string, string)>());
        _pipelineService.ValidateYamlStrictAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new YamlValidationResultDto { IsValid = true });
        _pipelineService.CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new PipelineDto { Id = _nextPipelineId++, Name = call.Arg<CreatePipelineRequest>().Name });

        _libraries.GetLibrariesAsync(SourceProjectId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items = [new VariableLibraryDto { Id = 30, Name = "aetheus-prod-host", ProjectId = SourceProjectId }]
            });
        _libraries.GetLibrariesAsync(TargetProjectId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>());
        _libraries.ExportEntriesAsync(30, Arg.Any<CancellationToken>())
            .Returns([new VariableEntryDto { Key = "HOST", Value = "vps.aetheus.example" }, new VariableEntryDto { Key = "PORT", Value = "10025" }]);
        _libraries.CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new VariableLibraryDto { Id = 300, Name = call.Arg<CreateVariableLibraryRequest>().Name, ProjectId = TargetProjectId });

        _vaults.GetVaultsAsync(SourceProjectId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = 40, Name = "aetheus-deploy", ProjectId = SourceProjectId }]
            });
        _vaults.GetVaultsAsync(TargetProjectId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>());
        _vaults.ExportSecretKeysAsync(40, Arg.Any<CancellationToken>()).Returns(["DB_PASSWORD"]);
        _vaults.CreateVaultAsync(Arg.Any<CreateVaultRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new VaultDto { Id = 400, Name = call.Arg<CreateVaultRequest>().Name, ProjectId = TargetProjectId });

        _readiness.CheckProjectAsync(TargetProjectId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineSetupReadinessDto
            {
                RepositoryInspected = true,
                Checks = [new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.MissingEnvironments, Items = ["prod"] }]
            });
    }

    private static PipelineProjectCopyRequest Request(params int[] pipelineIds) => new()
    {
        SourceProjectId = SourceProjectId,
        TargetProjectId = TargetProjectId,
        SourcePrefix = "aetheus",
        Prefix = "ats",
        TargetBranch = "develop",
        PipelineIds = [.. pipelineIds]
    };

    [Fact]
    public async Task Plan_AdaptsNamesAndReferences_AndNamesTheStructureWithoutAnyValue()
    {
        var plan = await _sut.PlanAsync(Request(), null, null, null, Ct);

        Assert.Equal(["ats-candidate", "ats-deploy-prod"], plan.Pipelines.Select(item => item.NewName));
        Assert.All(plan.Pipelines, item => Assert.Equal(PipelineCopyStatus.Ready, item.Status));
        var library = Assert.Single(plan.Libraries);
        Assert.Equal(("aetheus-prod-host", "ats-prod-host"), (library.SourceName, library.NewName));
        Assert.Equal(["HOST", "PORT"], library.Keys);
        var vault = Assert.Single(plan.Vaults);
        Assert.Equal("ats-deploy", vault.NewName);
        Assert.Equal(["DB_PASSWORD"], vault.Keys);
        await _pipelineService.DidNotReceive().CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>());
        await _libraries.DidNotReceive().CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Copy_WritesAdaptedPipelinesToTheNewProject_AndCopiesNamesNeverValues()
    {
        var created = new List<CreatePipelineRequest>();
        _pipelineService.When(service => service.CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => created.Add(call.Arg<CreatePipelineRequest>()));
        List<CreateVariableEntryRequest>? entries = null;
        await _libraries.ImportEntriesAsync(300, Arg.Do<List<CreateVariableEntryRequest>>(list => entries = list), Arg.Any<CancellationToken>());
        List<CreateVaultSecretRequest>? secrets = null;
        await _vaults.ImportSecretsAsync(400, Arg.Do<List<CreateVaultSecretRequest>>(list => secrets = list), Arg.Any<CancellationToken>());

        var result = await _sut.CopyAsync(Request(10, 11), 5, null, null, null, Ct);

        Assert.Equal(2, created.Count);
        var candidate = created.Single(request => request.Name == "ats-candidate");
        // The new project's repository and branch; nothing of the source project's bindings.
        Assert.Equal(TargetProjectId, candidate.ProjectId);
        Assert.Equal("develop", candidate.SourceBranch);
        Assert.Null(candidate.SourceRepositoryId);
        Assert.Null(candidate.EnvironmentId);
        Assert.Null(candidate.ProjectServerId);
        Assert.Contains("- ats-prod-host", candidate.YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("pipeline: ats-deploy-prod", candidate.YamlDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("aetheus", candidate.YamlDefinition, StringComparison.Ordinal);

        Assert.NotNull(entries);
        Assert.Equal(["HOST", "PORT"], entries.Select(entry => entry.Key));
        Assert.All(entries, entry => Assert.Equal(string.Empty, entry.Value));
        Assert.NotNull(secrets);
        Assert.Equal("DB_PASSWORD", Assert.Single(secrets).Key);
        Assert.Equal(string.Empty, secrets[0].Value);

        Assert.Equal(300, Assert.Single(result.Plan.Libraries).CreatedId);
        Assert.Equal(400, Assert.Single(result.Plan.Vaults).CreatedId);
        Assert.All(result.Plan.Pipelines, item => Assert.NotNull(item.CreatedPipelineId));
        // What remains is the new project's readiness verdict.
        Assert.Equal(PipelineSetupReadinessKind.MissingEnvironments, Assert.Single(result.Readiness.Checks).Kind);
    }

    /// <summary>R-76: the source project worked on develop, the new project's repository only has main; the copy must read main.</summary>
    [Fact]
    public async Task Copy_RewritesTheSourceBranchOfTheDefinition_ToTheBranchOfTheNewProject()
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(SourceProjectId, Arg.Any<CancellationToken>())
            .Returns([(12, "aetheus-ci", "name: aetheus-ci\ntrigger: manual\nsource_branch: \"develop\"\nstages: []\n")]);
        var created = new List<CreatePipelineRequest>();
        _pipelineService.When(service => service.CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => created.Add(call.Arg<CreatePipelineRequest>()));

        await _sut.CopyAsync(Request(12) with { TargetBranch = "main" }, null, null, null, null, Ct);

        var copy = Assert.Single(created);
        Assert.Equal("main", copy.SourceBranch);
        Assert.Contains("\nsource_branch: \"main\"\n", copy.YamlDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("develop", copy.YamlDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_WithoutATargetBranch_WritesNoSourceBranchLine_AsTheWizardDoes()
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(SourceProjectId, Arg.Any<CancellationToken>())
            .Returns([(12, "aetheus-ci", "name: aetheus-ci\ntrigger: manual\nsource_branch: \"develop\"\nstages: []\n")]);
        var created = new List<CreatePipelineRequest>();
        _pipelineService.When(service => service.CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => created.Add(call.Arg<CreatePipelineRequest>()));

        await _sut.CopyAsync(Request(12) with { TargetBranch = null }, null, null, null, null, Ct);

        var copy = Assert.Single(created);
        Assert.Null(copy.SourceBranch);
        Assert.DoesNotContain("source_branch", copy.YamlDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_OfOnePipeline_ReportsTheReferenceToAPipelineNotCopied()
    {
        var result = await _sut.CopyAsync(Request(10), null, null, null, null, Ct);

        var item = Assert.Single(result.Plan.Pipelines);
        Assert.Equal(PipelineCopyStatus.ToReview, item.Status);
        var issue = Assert.Single(item.Issues, found => found.Kind == PipelineCopyIssueKind.UncopiedPipeline);
        Assert.Contains("aetheus-deploy-prod", issue.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_ANameAlreadyTakenInTheNewProject_IsNotCopied()
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(TargetProjectId, Arg.Any<CancellationToken>())
            .Returns([(90, "ats-deploy-prod", DeployYaml)]);

        var result = await _sut.CopyAsync(Request(10, 11), null, null, null, null, Ct);

        var deploy = result.Plan.Pipelines.Single(item => item.SourceName == "aetheus-deploy-prod");
        Assert.Equal(PipelineCopyStatus.NotCopied, deploy.Status);
        Assert.Equal(PipelineCopyIssueKind.NameTaken, Assert.Single(deploy.Issues).Kind);
        Assert.Null(deploy.CreatedPipelineId);
        await _pipelineService.DidNotReceive().CreatePipelineAsync(
            Arg.Is<CreatePipelineRequest>(request => request.Name == "ats-deploy-prod"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Copy_AFailureRemovesEverythingTheCallCreated()
    {
        _pipelineService.CreatePipelineAsync(Arg.Is<CreatePipelineRequest>(request => request.Name == "ats-deploy-prod"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConflictException("git refused"));

        await Assert.ThrowsAsync<ConflictException>(() => _sut.CopyAsync(Request(10, 11), null, null, null, null, Ct));

        await _pipelineService.Received(1).DeletePipelineAsync(500, Arg.Any<CancellationToken>());
        await _vaults.Received(1).DeleteVaultAsync(400, Arg.Any<CancellationToken>());
        await _libraries.Received(1).DeleteLibraryAsync(300, Arg.Any<CancellationToken>());
    }

    private const string NightlyYaml = """
        name: aetheus-nightly
        trigger: schedule
        schedule: "0 0 3 * * *"
        variables:
          COMPOSE_PROJECT_NAME: aetheus-nightly-run
          SITE: aetheus.example.com
        stages:
          - name: Run
            environment: aetheus-staging
            steps:
              - name: go
                shell: sh .pipeline/scripts/nightly.sh
        """;

    private void SourceHas(string name, string yaml) =>
        _pipelines.GetPipelineDefinitionsForProjectAsync(SourceProjectId, Arg.Any<CancellationToken>()).Returns([(20, name, yaml)]);

    [Fact]
    public async Task Copy_OfAScheduledPipeline_IsCreatedWithItsTriggerOff_AndTheResultSaysSo()
    {
        SourceHas("aetheus-nightly", NightlyYaml);
        CreatePipelineRequest? created = null;
        _pipelineService.When(service => service.CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => created = call.Arg<CreatePipelineRequest>());

        var result = await _sut.CopyAsync(Request(20), null, null, null, null, Ct);

        Assert.NotNull(created);
        Assert.Contains("trigger: manual", created.YamlDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("trigger: schedule", created.YamlDefinition, StringComparison.Ordinal);
        Assert.Equal("manual", YamlParsingHelper.ParseAndValidate(created.YamlDefinition)!.Trigger);
        var item = Assert.Single(result.Plan.Pipelines);
        Assert.Equal(PipelineCopyStatus.ToReview, item.Status);
        Assert.Equal("schedule", Assert.Single(item.Issues, issue => issue.Kind == PipelineCopyIssueKind.TriggerDisabled).Detail);
    }

    [Fact]
    public async Task Plan_RenamesTheIdentifiersItCanIdentify_AndListsWhatItLeaves()
    {
        SourceHas("aetheus-nightly", NightlyYaml);

        var plan = await _sut.PlanAsync(Request(20), null, null, null, Ct);

        var issues = Assert.Single(plan.Pipelines).Issues;
        var renamed = Assert.Single(issues, issue => issue.Kind == PipelineCopyIssueKind.IdentifiersRenamed).Detail;
        Assert.Contains("COMPOSE_PROJECT_NAME: aetheus-nightly-run -> ats-nightly-run", renamed, StringComparison.Ordinal);
        Assert.Contains("environment: aetheus-staging -> ats-staging", renamed, StringComparison.Ordinal);
        // The address is not an identifier the copy can rename safely: it stays, and is reported.
        var remaining = Assert.Single(issues, issue => issue.Kind == PipelineCopyIssueKind.SourceProjectNameRemains).Detail;
        Assert.Contains("aetheus.example.com", remaining, StringComparison.Ordinal);
        Assert.DoesNotContain("aetheus-staging", remaining, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_NamesTheRepositoryFilesTheCopiedPipelineNeeds()
    {
        SourceHas("aetheus-nightly", NightlyYaml);

        var result = await _sut.CopyAsync(Request(20), null, null, null, null, Ct);

        var issue = Assert.Single(Assert.Single(result.Plan.Pipelines).Issues,
            found => found.Kind == PipelineCopyIssueKind.RepositoryFileDependency);
        Assert.Equal(".pipeline/scripts/nightly.sh", issue.Detail);
    }

    [Fact]
    public async Task Copy_OfAManualPipelineWhoseOnlyFindingIsARenamedIdentifier_StaysReady()
    {
        SourceHas("aetheus-ci", "name: aetheus-ci\ntrigger: manual\nvariables:\n  COMPOSE_PROJECT_NAME: aetheus-ci-run\nstages: []\n");

        var plan = await _sut.PlanAsync(Request(20), null, null, null, Ct);

        var item = Assert.Single(plan.Pipelines);
        Assert.Equal(PipelineCopyStatus.Ready, item.Status);
        Assert.Equal(PipelineCopyIssueKind.IdentifiersRenamed, Assert.Single(item.Issues).Kind);
    }

    [Fact]
    public async Task Copy_WhenATriggerCannotBeSwitchedOffSafely_TheCopyIsRefused()
    {
        SourceHas("aetheus-flow", "{name: aetheus-flow, trigger: schedule, schedule: '0 0 3 * * *', stages: []}");

        var result = await _sut.CopyAsync(Request(20), null, null, null, null, Ct);

        var item = Assert.Single(result.Plan.Pipelines);
        Assert.Equal(PipelineCopyStatus.NotCopied, item.Status);
        Assert.Contains(item.Issues, issue => issue.Kind == PipelineCopyIssueKind.InvalidDefinition);
        await _pipelineService.DidNotReceive().CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    // Recette R-86: a generic template reads `${{ project.slug }}.share`, so the source project's libraries
    // and vaults are named after ITS slug (`w7.share`); the copies must carry the TARGET project's slug,
    // the name the copied pipelines will read, not the source's.
    [Fact]
    public async Task Copy_OfSlugNamedLibrariesAndVaults_NamesThemAfterTheTargetProject()
    {
        _pipelines.GetProjectNameAsync(SourceProjectId, Arg.Any<CancellationToken>()).Returns("W7");
        _pipelines.GetProjectNameAsync(TargetProjectId, Arg.Any<CancellationToken>()).Returns("R2 Probe");
        SourceHas("w7-candidate", """
            name: w7-candidate
            trigger: manual
            variable_libraries:
              - ${{ project.slug }}.share
              - w7.prod
            vaults:
              - ${{ project.slug }}.prod
            stages: []
            """);
        _libraries.GetLibrariesAsync(SourceProjectId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items =
                [
                    new VariableLibraryDto { Id = 31, Name = "w7.share", ProjectId = SourceProjectId },
                    new VariableLibraryDto { Id = 32, Name = "w7.prod", ProjectId = SourceProjectId }
                ]
            });
        _libraries.ExportEntriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([new VariableEntryDto { Key = "HOST", Value = "x" }]);
        _vaults.GetVaultsAsync(SourceProjectId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = 41, Name = "w7.prod", ProjectId = SourceProjectId }]
            });
        _vaults.ExportSecretKeysAsync(41, Arg.Any<CancellationToken>()).Returns(["DB_PASSWORD"]);
        var createdLibraries = new List<string>();
        _libraries.When(service => service.CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => createdLibraries.Add(call.Arg<CreateVariableLibraryRequest>().Name));
        var createdVaults = new List<string>();
        _vaults.When(service => service.CreateVaultAsync(Arg.Any<CreateVaultRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => createdVaults.Add(call.Arg<CreateVaultRequest>().Name));
        CreatePipelineRequest? pipeline = null;
        _pipelineService.When(service => service.CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => pipeline = call.Arg<CreatePipelineRequest>());

        var result = await _sut.CopyAsync(
            Request(20) with { SourcePrefix = "w7", Prefix = "r2probe" }, null, null, null, null, Ct);

        Assert.Equal(["r2-probe.share", "r2-probe.prod"], createdLibraries);
        Assert.Equal(["r2-probe.prod"], createdVaults);
        Assert.Equal(["w7.share", "w7.prod"], result.Plan.Libraries.Select(item => item.SourceName));
        // A literal source name in the definition follows its copy; the expression is left for the run to expand.
        Assert.NotNull(pipeline);
        Assert.Contains("- r2-probe.prod", pipeline.YamlDefinition, StringComparison.Ordinal);
        Assert.Contains("${{ project.slug }}.share", pipeline.YamlDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("w7.", pipeline.YamlDefinition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_IntoTheSameProject_IsRefused() =>
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.PlanAsync(
            Request() with { TargetProjectId = SourceProjectId }, null, null, null, Ct));

    [Fact]
    public async Task Plan_OnlyOffersThePipelinesTheCallerCanRead()
    {
        var plan = await _sut.PlanAsync(Request(), [11], null, null, Ct);

        Assert.Equal("ats-deploy-prod", Assert.Single(plan.Pipelines).NewName);
        Assert.Empty(plan.Libraries);
    }
}
