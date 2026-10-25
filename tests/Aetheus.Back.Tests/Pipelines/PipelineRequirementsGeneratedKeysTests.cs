// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Shared.Components.PortRegistry;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R2-21: provisioning gives a value to the keys the generic templates read when nothing leaves
/// a doubt (random secrets, ports of the registry, names derived from the project) and leaves the rest
/// empty.</summary>
public sealed class PipelineRequirementsGeneratedKeysTests
{
    private const int ProjectId = 7;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPipelineSetupReadinessService _readiness = Substitute.For<IPipelineSetupReadinessService>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();
    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly PipelineRequirementsProvisioner _sut;

    public PipelineRequirementsGeneratedKeysTests()
    {
        _sut = new PipelineRequirementsProvisioner(
            _readiness, _pipelines, Substitute.For<IPipelineRequirementsChecker>(), Substitute.For<IPipelineTemplateResolver>(),
            _libraries, _vaults, NullLogger<PipelineRequirementsProvisioner>.Instance);
        _pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Wordmorrow");
        _libraries.CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new VariableLibraryDto { Id = 101, Name = call.Arg<CreateVariableLibraryRequest>().Name, ProjectId = ProjectId });
        _vaults.CreateVaultAsync(Arg.Any<CreateVaultRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new VaultDto { Id = 201, Name = call.Arg<CreateVaultRequest>().Name, ProjectId = ProjectId });
        _libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>());
        _vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>());
    }

    private void Missing(PipelineSetupReadinessKind kind, string name) =>
        _readiness.CheckAsync(ProjectId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineSetupReadinessDto
            {
                Checks = [new PipelineSetupReadinessCheckDto { Kind = kind, Severity = PipelineSetupReadinessSeverity.Blocking, Items = [name] }]
            });

    private List<CreateVariableEntryRequest> Entries() =>
        [.. _libraries.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IVariableLibraryService.ImportEntriesAsync))
            .SelectMany(call => (List<CreateVariableEntryRequest>)call.GetArguments()[1]!)];

    [Fact]
    public async Task TheProductionLibrary_GetsNamesDerivedFromTheProject_AndEmptyDomainAndContact()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredLibraries, "wordmorrow.prod");

        var result = await _sut.ProvisionAsync(ProjectId, ["application-deploy-prod"], null, null, null, Ct);

        var entries = Entries().ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal("wordmorrow-prod", entries["COMPOSE_PROJECT"]);
        Assert.Equal("/var/lib/wordmorrow-production", entries["STATE_DIR"]);
        // ADR-057: no environment file path exists any more, the deployment renders its own from the vault and the libraries.
        Assert.DoesNotContain("ENV_FILE", entries.Keys);
        Assert.Equal(string.Empty, entries["DOMAIN"]);
        Assert.Equal(string.Empty, entries["VERSION_SUFFIX"]);
        var library = Assert.Single(result.Libraries);
        Assert.Contains("COMPOSE_PROJECT", library.FilledKeys);
        Assert.DoesNotContain("DOMAIN", library.FilledKeys);
        // The ports are the production setup's business (it reads them from the Compose file and reserves them), not the generic provisioning's.
        Assert.DoesNotContain(entries.Keys, key => key.StartsWith("PORT_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheShareLibrary_GetsTheAcmeWebRoot_TheHttpVirtualHostsServeTheChallengeFrom()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredLibraries, "wordmorrow.share");

        await _sut.ProvisionAsync(ProjectId, ["application-deploy-prod"], null, null, null, Ct);

        var entries = Entries().ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal("/var/www/aetheus-acme", entries["ACME_WEBROOT"]);
    }

    [Fact]
    public async Task TheProductionVault_GetsFreshRandomSecrets_DifferentOnEveryCreation()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "wordmorrow.prod");
        var imported = new List<List<CreateVaultSecretRequest>>();
        _vaults.ImportSecretsAsync(201, Arg.Do<List<CreateVaultSecretRequest>>(imported.Add), Arg.Any<CancellationToken>()).Returns(4);

        var first = await _sut.ProvisionAsync(ProjectId, ["application-deploy-prod"], null, null, null, Ct);
        await _sut.ProvisionAsync(ProjectId, ["application-deploy-prod"], null, null, null, Ct);

        Assert.Equal(2, imported.Count);
        var keys = imported[0].Select(secret => secret.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["DB_PASSWORD", "ENCRYPTION_KEY", "ENCRYPTION_SALT", "JWT_KEY"], keys);
        Assert.All(imported[0], secret => Assert.False(string.IsNullOrWhiteSpace(secret.Value)));
        Assert.NotEqual(
            imported[0].OrderBy(secret => secret.Key, StringComparer.Ordinal).Select(secret => secret.Value),
            imported[1].OrderBy(secret => secret.Key, StringComparer.Ordinal).Select(secret => secret.Value));
        Assert.Equal(4, Assert.Single(first.Vaults).FilledKeys.Count);
    }

    [Fact]
    public async Task TheQaVault_AlsoGetsTheSmokeAdministratorPassword()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "wordmorrow.qa");
        List<CreateVaultSecretRequest>? imported = null;
        _vaults.ImportSecretsAsync(201, Arg.Do<List<CreateVaultSecretRequest>>(secrets => imported = secrets), Arg.Any<CancellationToken>()).Returns(5);

        await _sut.ProvisionAsync(ProjectId, ["application-qa"], null, null, null, Ct);

        var smoke = Assert.Single(imported!, secret => secret.Key == "SMOKE_ADMIN_PASSWORD");
        Assert.Equal(24, smoke.Value.Length);
    }

    [Fact]
    public async Task AResourceTheCatalogDoesNotKnow_KeepsItsCopiedKeysEmpty()
    {
        Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "wordmorrow.package");
        _vaults.GetVaultsAsync(null, null, null, Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = 5, Name = "wordmorrow.package", ProjectId = 1, ProjectName = "Other" }]
            });
        _vaults.ExportSecretKeysAsync(5, Arg.Any<CancellationToken>()).Returns(["NUGET_API_KEY"]);

        var result = await _sut.ProvisionAsync(ProjectId, ["publish-observability-packages"], null, [], [5], Ct);

        Assert.Empty(Assert.Single(result.Vaults).FilledKeys);
        await _vaults.Received(1).ImportSecretsAsync(
            201, Arg.Is<List<CreateVaultSecretRequest>>(secrets => secrets.Count == 1 && secrets[0].Value.Length == 0), Arg.Any<CancellationToken>());
    }
}
