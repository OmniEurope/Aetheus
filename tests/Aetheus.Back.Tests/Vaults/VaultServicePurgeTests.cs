// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Purging historical versions only touches a secret of the named vault, and an audit entry
/// is written only when something was actually removed.</summary>
public class VaultServicePurgeTests
{
    private static readonly DateTime Cutoff = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IVaultRepository _repo = Substitute.For<IVaultRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly VaultService _sut;

    public VaultServicePurgeTests()
    {
        _sut = new VaultService(_repo, Substitute.For<IEncryptionService>(), Substitute.For<IDbTransactionScope>(),
            _audit, Substitute.For<IEntityChangeNotifier>(), TimeProvider.System, Substitute.For<IMemoryCache>());
    }

    [Fact]
    public async Task UnknownSecret_PurgesNothing()
    {
        _repo.FindSecretAsync(9, Arg.Any<CancellationToken>()).Returns((VaultSecret?)null);

        Assert.Equal(0, await _sut.PurgeHistoricalSecretVersionsAsync(1, 9, Cutoff, 10, TestContext.Current.CancellationToken));

        await _repo.DidNotReceiveWithAnyArgs().PurgeHistoricalSecretVersionsAsync(default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SecretOfAnotherVault_PurgesNothing()
    {
        _repo.FindSecretAsync(3, Arg.Any<CancellationToken>()).Returns(new VaultSecret { Id = 3, VaultId = 2, Key = "K" });

        Assert.Equal(0, await _sut.PurgeHistoricalSecretVersionsAsync(1, 3, Cutoff, 10, TestContext.Current.CancellationToken));

        await _repo.DidNotReceiveWithAnyArgs().PurgeHistoricalSecretVersionsAsync(default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PurgedVersions_AreAudited()
    {
        _repo.FindSecretAsync(3, Arg.Any<CancellationToken>()).Returns(new VaultSecret { Id = 3, VaultId = 1, Key = "K" });
        _repo.PurgeHistoricalSecretVersionsAsync(3, Cutoff, 10, Arg.Any<CancellationToken>()).Returns(4);

        Assert.Equal(4, await _sut.PurgeHistoricalSecretVersionsAsync(1, 3, Cutoff, 10, TestContext.Current.CancellationToken));

        await _audit.Received(1).LogAsync("PurgedSecretVersions", "VaultSecret", 3, "4 historical versions", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NothingToPurge_WritesNoAuditEntry()
    {
        _repo.FindSecretAsync(3, Arg.Any<CancellationToken>()).Returns(new VaultSecret { Id = 3, VaultId = 1, Key = "K" });
        _repo.PurgeHistoricalSecretVersionsAsync(3, Cutoff, 10, Arg.Any<CancellationToken>()).Returns(0);

        Assert.Equal(0, await _sut.PurgeHistoricalSecretVersionsAsync(1, 3, Cutoff, 10, TestContext.Current.CancellationToken));

        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, TestContext.Current.CancellationToken);
    }
}
