// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class VaultServiceResolveTests
{
    private readonly IVaultRepository _repo = Substitute.For<IVaultRepository>();
    private readonly VaultService _sut;

    public VaultServiceResolveTests()
    {
        var encryption = Substitute.For<IEncryptionService>();
        // Identity decryption so assertions read the stored ciphertext placeholder directly.
        encryption.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

        _sut = new VaultService(_repo, encryption, Substitute.For<IDbTransactionScope>(),
            Substitute.For<IAuditService>(), Substitute.For<IEntityChangeNotifier>(),
            TimeProvider.System, Substitute.For<IMemoryCache>());
    }

    private static Vault Vault(int? projectId, int? envId, int? projectServerId, string key, string value, int id = 0) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            EnvironmentId = envId,
            ProjectServerId = projectServerId,
            Secrets = [new VaultSecret { Key = key, EncryptedValue = value }]
        };

    [Fact]
    public async Task ResolveWithCrossAccess_ProjectServerScopeWins()
    {
        _repo.FindByNamesWithCrossAccessAsync(Arg.Any<List<string>>(), 1, Arg.Any<CancellationToken>()).Returns(
        [
            Vault(null, null, null, "TOKEN", "global"),
            Vault(1, null, null, "TOKEN", "project"),
            Vault(null, 2, null, "TOKEN", "environment"),
            Vault(null, null, 3, "TOKEN", "projectserver")
        ]);

        var result = await _sut.ResolveVaultSecretsWithCrossAccessAsync(["any"], 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("projectserver", result["TOKEN"]);
    }

    [Fact]
    public async Task ResolveWithCrossAccess_MergesDistinctKeys()
    {
        _repo.FindByNamesWithCrossAccessAsync(Arg.Any<List<string>>(), 1, Arg.Any<CancellationToken>()).Returns(
        [
            Vault(null, null, null, "A", "1"),
            Vault(null, 2, null, "B", "2")
        ]);

        var result = await _sut.ResolveVaultSecretsWithCrossAccessAsync(["any"], 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
    }

    /// <summary>R2-27: the summary links an empty secret to the vault whose value a run reads (the scope
    /// precedence), not another vault holding the same name.</summary>
    [Fact]
    public async Task SecretOwners_NameTheVaultWhoseValueWins_PerKey()
    {
        _repo.FindByNamesWithCrossAccessAsync(Arg.Any<List<string>>(), 1, Arg.Any<CancellationToken>()).Returns(
        [
            Vault(null, null, null, "DB_PASSWORD", "", id: 20),
            Vault(1, null, null, "DB_PASSWORD", "", id: 21),
            Vault(null, null, null, "JWT_KEY", "", id: 20)
        ]);

        var owners = await _sut.ResolveSecretOwnersWithCrossAccessAsync(["any"], 1, TestContext.Current.CancellationToken);

        Assert.Equal(21, owners["db_password"]);
        Assert.Equal(20, owners["JWT_KEY"]);
    }
}
