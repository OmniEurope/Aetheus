// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Shared.Components.PortRegistry;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Provisioning creates exactly the missing requirements, copies names without values and
/// reads only authorized source resources.</summary>
public sealed class PipelineRequirementsProvisionerTests
{
    private const int ProjectId = 7;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPipelineSetupReadinessService _readiness = Substitute.For<IPipelineSetupReadinessService>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();
    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRequirementsChecker _checker = Substitute.For<IPipelineRequirementsChecker>();
    private readonly IPipelineTemplateResolver _templates = Substitute.For<IPipelineTemplateResolver>();
    private readonly PipelineRequirementsProvisioner _sut;

    public PipelineRequirementsProvisionerTests()
    {
        _sut = new PipelineRequirementsProvisioner(
            _readiness, _pipelines, _checker, _templates, _libraries, _vaults, NullLogger<PipelineRequirementsProvisioner>.Instance);
        _libraries.CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new VariableLibraryDto { Id = 101, Name = call.Arg<CreateVariableLibraryRequest>().Name, ProjectId = ProjectId });
        _vaults.CreateVaultAsync(Arg.Any<CreateVaultRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new VaultDto { Id = 201, Name = call.Arg<CreateVaultRequest>().Name, ProjectId = ProjectId });
        _libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>());
        _vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>());
    }

    private void Missing(PipelineSetupReadinessKind kind, params string[] names) =>
        _readiness.CheckAsync(ProjectId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineSetupReadinessDto
            {
                Checks =
                [
                    new PipelineSetupReadinessCheckDto { Kind = kind, Severity = PipelineSetupReadinessSeverity.Blocking, Items = [.. names] },
                    // A finding of another kind must never turn into a created resource.
                    new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.MissingEnvironments, Items = ["qa"] }
                ]
            });

    [Fact]
    public async Task CopiesTheKeyNamesOfTheReadableSource_AndLeavesEveryValueEmpty()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredLibraries, "aetheus-prod-host");
        _libraries.GetLibrariesAsync(null, null, null, Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items =
                [
                    // A substring match the search also returns: never a source.
                    new VariableLibraryDto { Id = 9, Name = "aetheus-prod-host-old", ProjectId = 1, ProjectName = "Aetheus" },
                    new VariableLibraryDto { Id = 5, Name = "aetheus-prod-host", ProjectId = 1, ProjectName = "Aetheus", UpdatedAt = new DateTime(2026, 9, 1) }
                ]
            });
        _libraries.ExportEntriesAsync(5, Arg.Any<CancellationToken>()).Returns(
        [
            new VariableEntryDto { Key = "HOST", Value = "vps2577917.example" },
            new VariableEntryDto { Key = "PORT_FRONT", Value = "10025" }
        ]);

        var result = await _sut.ProvisionAsync(ProjectId, ["aetheus-candidate"], null, [5, 9], null, Ct);

        var library = Assert.Single(result.Libraries);
        Assert.Equal(["HOST", "PORT_FRONT"], library.Keys);
        Assert.Equal("Aetheus / aetheus-prod-host", library.KeysCopiedFrom);
        Assert.Empty(result.Vaults);
        await _libraries.Received(1).CreateLibraryAsync(
            Arg.Is<CreateVariableLibraryRequest>(request => request.Name == "aetheus-prod-host" && request.ProjectId == ProjectId),
            Arg.Any<CancellationToken>());
        // The other project's host and port must not travel: every created value is empty.
        await _libraries.Received(1).ImportEntriesAsync(
            101,
            Arg.Is<List<CreateVariableEntryRequest>>(entries =>
                entries.Count == 2 && entries.All(entry => entry.Value.Length == 0)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Dotted library names match exactly, not by shared prefix.</summary>
    [Fact]
    public async Task ADottedLibraryName_IsCreatedAsWritten_WithTheKeysOfTheSameName()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredLibraries, "aetheus.share");
        _libraries.GetLibrariesAsync(null, null, null, Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items =
                [
                    new VariableLibraryDto { Id = 8, Name = "aetheus.shared", ProjectId = 1, ProjectName = "Aetheus" },
                    new VariableLibraryDto { Id = 6, Name = "aetheus.share", ProjectId = 1, ProjectName = "Aetheus" }
                ]
            });
        _libraries.ExportEntriesAsync(6, Arg.Any<CancellationToken>()).Returns(
        [
            new VariableEntryDto { Key = "APP_SUBDOMAIN", Value = "app" },
            new VariableEntryDto { Key = "HOST_PREFIX", Value = "" }
        ]);

        var result = await _sut.ProvisionAsync(ProjectId, ["aetheus-deploy-prod"], null, [6, 8], null, Ct);

        var library = Assert.Single(result.Libraries);
        Assert.Equal("aetheus.share", library.Name);
        Assert.Equal(["APP_SUBDOMAIN", "HOST_PREFIX"], library.Keys);
        await _libraries.Received(1).CreateLibraryAsync(
            Arg.Is<CreateVariableLibraryRequest>(request => request.Name == "aetheus.share"),
            Arg.Any<CancellationToken>());
        await _libraries.DidNotReceive().ExportEntriesAsync(8, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnlyReadsKeyNamesFromWhatTheCallerMayRead()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredLibraries, "aetheus-prod-host");
        List<int> readable = [42];

        await _sut.ProvisionAsync(ProjectId, ["aetheus-candidate"], null, readable, null, Ct);

        await _libraries.Received(1).GetLibrariesAsync(
            null, null, null, Arg.Any<PaginationRequest?>(), readable, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoReadableSource_CreatesTheResourceEmpty_AndSaysSo()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "aetheus-prod-secrets");

        var result = await _sut.ProvisionAsync(ProjectId, ["aetheus-candidate"], null, null, [], Ct);

        var vault = Assert.Single(result.Vaults);
        Assert.Empty(vault.Keys);
        Assert.Null(vault.KeysCopiedFrom);
        await _vaults.DidNotReceive().ImportSecretsAsync(Arg.Any<int>(), Arg.Any<List<CreateVaultSecretRequest>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NeverReadsKeysFromTheProjectBeingProvisioned_AndCreatesNothingForOtherFindings()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "aetheus-prod-secrets");
        _vaults.GetVaultsAsync(null, null, null, Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = 3, Name = "aetheus-prod-secrets", ProjectId = ProjectId }]
            });

        var result = await _sut.ProvisionAsync(ProjectId, ["aetheus-candidate"], null, null, null, Ct);

        Assert.Null(Assert.Single(result.Vaults).KeysCopiedFrom);
        await _vaults.DidNotReceive().ExportSecretKeysAsync(3, Arg.Any<CancellationToken>());
        // The MissingEnvironments finding is reported by the wizard, never "fixed" by this button.
        await _libraries.DidNotReceive().CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CopiesTheSecretNamesOfTheReadableVault_AndLeavesEverySecretEmpty()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "aetheus-prod-secrets");
        _vaults.GetVaultsAsync(null, null, null, Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = 4, Name = "aetheus-prod-secrets", ProjectId = 1, ProjectName = "Aetheus" }]
            });
        _vaults.ExportSecretKeysAsync(4, Arg.Any<CancellationToken>()).Returns(["DB_PASSWORD", "SMTP_PASSWORD"]);

        var result = await _sut.ProvisionAsync(ProjectId, ["aetheus-candidate"], null, null, [4], Ct);

        Assert.Equal(["DB_PASSWORD", "SMTP_PASSWORD"], Assert.Single(result.Vaults).Keys);
        await _vaults.Received(1).ImportSecretsAsync(
            201,
            Arg.Is<List<CreateVaultSecretRequest>>(secrets =>
                secrets.Count == 2 && secrets.All(secret => secret.Value.Length == 0)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenTheKeysCannotBeImported_TheKeylessLibraryIsRemoved_AndTheFailureSurfaces()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredLibraries, "aetheus-prod-host");
        _libraries.GetLibrariesAsync(null, null, null, Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items = [new VariableLibraryDto { Id = 5, Name = "aetheus-prod-host", ProjectId = 1, ProjectName = "Aetheus" }]
            });
        _libraries.ExportEntriesAsync(5, Arg.Any<CancellationToken>()).Returns([new VariableEntryDto { Key = "HOST", Value = "h" }]);
        _libraries.ImportEntriesAsync(101, Arg.Any<List<CreateVariableEntryRequest>>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("import failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ProvisionAsync(ProjectId, ["aetheus-candidate"], null, null, null, Ct));

        // A keyless library would satisfy the requirement and never be offered again: it must not stay.
        await _libraries.Received(1).DeleteLibraryAsync(101, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenTheSecretNamesCannotBeImported_TheEmptyVaultIsRemoved_AndTheFailureSurfaces()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "aetheus-prod-secrets");
        _vaults.GetVaultsAsync(null, null, null, Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = 4, Name = "aetheus-prod-secrets", ProjectId = 1, ProjectName = "Aetheus" }]
            });
        _vaults.ExportSecretKeysAsync(4, Arg.Any<CancellationToken>()).Returns(["DB_PASSWORD"]);
        _vaults.ImportSecretsAsync(201, Arg.Any<List<CreateVaultSecretRequest>>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("import failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ProvisionAsync(ProjectId, ["aetheus-candidate"], null, null, null, Ct));

        await _vaults.Received(1).DeleteVaultAsync(201, Arg.Any<CancellationToken>());
    }

    private const string NeedsHostLibrary = """
        name: app-prod
        requires:
          libraries: [aetheus-prod-host]
          vaults: [aetheus-prod-secrets]
        stages:
          - name: Deploy
            steps:
              - name: noop
        """;

    [Fact]
    public async Task Unmet_NamesEachMissingResourceOnce_WithEveryPipelineWaitingForIt()
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([(1, "app-prod", NeedsHostLibrary), (2, "app-qa", NeedsHostLibrary.Replace("app-prod", "app-qa"))]);
        _checker.CheckAsync(Arg.Any<PipelineRequiresDefinition?>(), ProjectId, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new PipelineRequirementOutcome(PipelineRequirementsChecker.LibraryKind, "aetheus-prod-host", Unmet),
                new PipelineRequirementOutcome(PipelineRequirementsChecker.VaultKind, "aetheus-prod-secrets", null),
                // An environment the pipeline also needs is not this warning's business.
                new PipelineRequirementOutcome(PipelineRequirementsChecker.EnvironmentKind, "qa", Unmet)
            ]);

        var unmet = await _sut.GetUnmetAsync(ProjectId, null, Ct);

        var library = Assert.Single(unmet);
        Assert.Equal(PipelineRequirementsChecker.LibraryKind, library.Kind);
        Assert.Equal("aetheus-prod-host", library.Name);
        Assert.Equal(["app-prod", "app-qa"], library.Pipelines);
        // One check for everything the project's pipelines reference, not one per pipeline.
        await _checker.Received(1).CheckAsync(
            Arg.Any<PipelineRequiresDefinition?>(), ProjectId, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unmet_IsEmptyWhenEveryRequirementIsSatisfied()
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([(1, "app-prod", NeedsHostLibrary)]);
        _checker.CheckAsync(Arg.Any<PipelineRequiresDefinition?>(), ProjectId, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([new PipelineRequirementOutcome(PipelineRequirementsChecker.LibraryKind, "aetheus-prod-host", null)]);

        Assert.Empty(await _sut.GetUnmetAsync(ProjectId, null, Ct));
    }

    [Fact]
    public async Task Unmet_ReadsTheResolvedTemplate_ForAWizardPipelineThatOnlyExtendsIt()
    {
        // The wizard stores `extends: template@v` and nothing else: the libraries and vaults are the
        // template's. The launch resolves the template; the warning must read the same definition.
        const string wizardPipeline = "name: app-prod\nextends: aetheus-candidate@3\n";
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([(5, "app-prod", wizardPipeline)]);
        _pipelines.GetPipelineOrganizationIdAsync(5, Arg.Any<CancellationToken>()).Returns(9);
        _templates.ResolveAsync(wizardPipeline, 9, null, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateResolution(
                new PipelineYamlDefinition { Name = "app-prod", VariableLibraries = ["aetheus-prod-host"], Vaults = ["aetheus-prod-secrets"] },
                wizardPipeline, "aetheus-candidate", 3, 3, false));
        PipelineRequiresDefinition? checkedFor = null;
        _checker.CheckAsync(Arg.Do<PipelineRequiresDefinition?>(definition => checkedFor = definition), ProjectId, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new PipelineRequirementOutcome(PipelineRequirementsChecker.LibraryKind, "aetheus-prod-host", Unmet),
                new PipelineRequirementOutcome(PipelineRequirementsChecker.VaultKind, "aetheus-prod-secrets", Unmet)
            ]);

        var unmet = await _sut.GetUnmetAsync(ProjectId, null, Ct);

        Assert.Equal(["aetheus-prod-host"], checkedFor!.Libraries);
        Assert.Equal(["aetheus-prod-secrets"], checkedFor.Vaults);
        Assert.Equal(2, unmet.Count);
        Assert.All(unmet, item => Assert.Equal(["app-prod"], item.Pipelines));
    }

    [Fact]
    public void ReferencedResources_JoinsRequiresWithTheListsThePipelineReads()
    {
        var referenced = PipelineRequirementsProvisioner.ReferencedResources(new PipelineYamlDefinition
        {
            Requires = new PipelineRequiresDefinition { Libraries = ["shared"], Vaults = ["keys"] },
            VariableLibraries = ["SHARED", "own"],
            Vaults = ["keys", ""]
        })!;

        Assert.Equal(["shared", "own"], referenced.Libraries);
        Assert.Equal(["keys"], referenced.Vaults);
        Assert.Null(PipelineRequirementsProvisioner.ReferencedResources(new PipelineYamlDefinition()));
    }

    private static readonly PipelineProblem Unmet = PipelineProblem.Of(PipelineProblemCodes.RequiredLibraryMissing, "missing");
}
