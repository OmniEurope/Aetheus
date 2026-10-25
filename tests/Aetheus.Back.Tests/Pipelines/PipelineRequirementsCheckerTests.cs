// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The `requires:` block. A generic template cannot name a host, so what it declares is what the
/// installation must supply, and the value of the declaration is entirely in it being VERIFIED: a
/// requirement nobody checks reads as a guarantee and is not one.
/// </summary>
public sealed class PipelineRequirementsCheckerTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();

    private PipelineRequirementsChecker BuildSut() => new(_repo, _libraries, _vaults);

    private void LibrariesFound(params string[] names) =>
        _libraries.ResolveLibrariesWithNamesAsync(
                Arg.Any<List<string>>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)));

    private void VaultsFound(params string[] names) =>
        _vaults.GetVaultNamesAsync(Arg.Any<int?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns([.. names]);

    private Task<IReadOnlyList<PipelineRequirementOutcome>> CheckAsync(PipelineRequiresDefinition requires) =>
        BuildSut().CheckAsync(requires, projectId: 7, organizationId: 3, TestContext.Current.CancellationToken);

    [Fact]
    public async Task NoDeclarationIsNoRequirement()
    {
        Assert.Empty(await BuildSut().CheckAsync(null, 7, 3, TestContext.Current.CancellationToken));
        Assert.Empty(await CheckAsync(new PipelineRequiresDefinition()));
    }

    [Fact]
    public async Task ADeclaredLibraryThatExistsIsSatisfied()
    {
        LibrariesFound("aetheus-prod-host");

        var outcome = Assert.Single(await CheckAsync(
            new PipelineRequiresDefinition { Libraries = ["aetheus-prod-host"] }));

        Assert.True(outcome.Satisfied);
        Assert.Equal(PipelineRequirementsChecker.LibraryKind, outcome.Kind);
    }

    [Fact]
    public async Task AMissingLibraryIsRefusedAndSaysWhatToCreate()
    {
        LibrariesFound();

        var outcome = Assert.Single(await CheckAsync(
            new PipelineRequiresDefinition { Libraries = ["aetheus-prod-host"] }));

        Assert.False(outcome.Satisfied);
        Assert.Contains("aetheus-prod-host", outcome.Problem!, StringComparison.Ordinal);
        Assert.Contains("Create it", outcome.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingVaultIsRefused()
    {
        VaultsFound("other");

        var outcome = Assert.Single(await CheckAsync(
            new PipelineRequiresDefinition { Vaults = ["aetheus-demo"] }));

        Assert.False(outcome.Satisfied);
        Assert.Equal(PipelineRequirementsChecker.VaultKind, outcome.Kind);
    }

    [Fact]
    public async Task AMissingEnvironmentIsRefused()
    {
        _repo.FindEnvironmentByNameAsync("qa", Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns((Environment?)null);

        var outcome = Assert.Single(await CheckAsync(
            new PipelineRequiresDefinition { Environments = ["qa"] }));

        Assert.False(outcome.Satisfied);
        Assert.Contains("qa", outcome.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADockerCapabilityIsCheckedAgainstTheFleet()
    {
        _repo.HasRunnerWithDockerAsync(3, Arg.Any<CancellationToken>()).Returns(false);

        var outcome = Assert.Single(await CheckAsync(
            new PipelineRequiresDefinition { Capabilities = ["docker"] }));

        Assert.False(outcome.Satisfied);
        Assert.Contains("Docker", outcome.Problem!, StringComparison.Ordinal);

        _repo.HasRunnerWithDockerAsync(3, Arg.Any<CancellationToken>()).Returns(true);
        Assert.True(Assert.Single(await CheckAsync(
            new PipelineRequiresDefinition { Capabilities = ["docker"] })).Satisfied);
    }

    [Fact]
    public async Task ACapabilityThisControlPlaneCannotVerifyIsRefusedRatherThanAccepted()
    {
        // Accepting it would let a template declare a guarantee with nothing behind it, which is the
        // one thing this block must not become.
        var outcome = Assert.Single(await CheckAsync(
            new PipelineRequiresDefinition { Capabilities = ["gpu"] }));

        Assert.False(outcome.Satisfied);
        Assert.Contains("can verify", outcome.Problem!, StringComparison.Ordinal);
        Assert.Contains("docker", outcome.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryUnmetRequirementIsReportedTogether()
    {
        LibrariesFound();
        VaultsFound();
        _repo.FindEnvironmentByNameAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((Environment?)null);

        var outcomes = await CheckAsync(new PipelineRequiresDefinition
        {
            Libraries = ["lib"],
            Vaults = ["vault"],
            Environments = ["env"]
        });

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.False(outcome.Satisfied));
    }

    /// <summary>R2-27: an empty value comes back with the id of the library or vault holding it, so the
    /// summary can link to it; a value whose holder is unknown has none.</summary>
    [Fact]
    public async Task AnEmptyValue_CarriesTheIdOfTheLibraryOrVaultHoldingIt()
    {
        _libraries.ResolveLibrariesWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string> { ["API_HOST"] = "", ["API_PORT"] = "", ["FILLED"] = "x" }, new HashSet<string>()));
        _libraries.ResolveEntryOwnersWithCrossAccessAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["API_HOST"] = 12, ["FILLED"] = 12 });
        _vaults.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string> { ["DB_PASSWORD"] = "" }, new HashSet<string>()));
        _vaults.ResolveSecretOwnersWithCrossAccessAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["DB_PASSWORD"] = 9 });

        var empty = await BuildSut().FindEmptyValuesAsync(["lib"], ["vault"], 7, TestContext.Current.CancellationToken);

        Assert.Equal(12, Assert.Single(empty, value => value.Key == "API_HOST").OwnerId);
        Assert.Null(Assert.Single(empty, value => value.Key == "API_PORT").OwnerId);
        Assert.Equal(9, Assert.Single(empty, value => value.Key == "DB_PASSWORD").OwnerId);
        Assert.DoesNotContain(empty, value => value.Key == "FILLED");
    }

    /// <summary>R-10: the table of empty values says the library or vault by name, read once per owner.</summary>
    [Fact]
    public async Task AnEmptyValue_CarriesTheNameOfTheLibraryOrVaultHoldingIt()
    {
        _libraries.ResolveLibrariesWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string> { ["HOST_PREFIX"] = "", ["API_PORT"] = "", ["ORPHAN"] = "" }, new HashSet<string>()));
        _libraries.ResolveEntryOwnersWithCrossAccessAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["HOST_PREFIX"] = 12, ["API_PORT"] = 12 });
        _libraries.GetLibraryDetailAsync(12, Arg.Any<CancellationToken>())
            .Returns(new VariableLibraryDetailDto { Id = 12, Name = "aetheus.prod" });
        _vaults.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string> { ["DB_PASSWORD"] = "" }, new HashSet<string>()));
        _vaults.ResolveSecretOwnersWithCrossAccessAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["DB_PASSWORD"] = 9 });
        _vaults.GetVaultDetailAsync(9, Arg.Any<CancellationToken>())
            .Returns(new VaultDetailDto { Id = 9, Name = "aetheus-deploy" });

        var empty = await BuildSut().FindEmptyValuesAsync(["lib"], ["vault"], 7, TestContext.Current.CancellationToken);

        Assert.Equal("aetheus.prod", Assert.Single(empty, value => value.Key == "HOST_PREFIX").OwnerName);
        Assert.Equal("aetheus.prod", Assert.Single(empty, value => value.Key == "API_PORT").OwnerName);
        Assert.Null(Assert.Single(empty, value => value.Key == "ORPHAN").OwnerName);
        Assert.Equal("aetheus-deploy", Assert.Single(empty, value => value.Key == "DB_PASSWORD").OwnerName);
        await _libraries.Received(1).GetLibraryDetailAsync(12, Arg.Any<CancellationToken>());
    }
}
