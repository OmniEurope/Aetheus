// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

public class VaultRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly VaultRepository _repo;
    private readonly int _projectId;

    public VaultRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new VaultRepository(_db, TimeProvider.System);

        var project = new Project { Name = "P" };
        _db.Projects.Add(project);
        _db.SaveChanges();
        _projectId = project.Id;
    }

    [Fact]
    public async Task GetVaultsPagedAsync_ReturnsPagedOrderedByName()
    {
        _db.Vaults.AddRange(
            new Vault { Name = "Zeta", Description = "d", ProjectId = _projectId },
            new Vault { Name = "Alpha", Description = "d", ProjectId = _projectId },
            new Vault { Name = "Mid", Description = "d", ProjectId = _projectId }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetVaultsPagedAsync(null, null, null, null, 1, 2, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, total);
        Assert.Equal(2, items.Count);
        Assert.Equal("Alpha", items[0].Name);
    }

    [Fact]
    public async Task GetVaultsPagedAsync_WithSearch_FiltersNameAndDescription()
    {
        _db.Vaults.AddRange(
            new Vault { Name = "Prod", Description = "Production vault" },
            new Vault { Name = "Dev", Description = "Development" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetVaultsPagedAsync("Prod", null, null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetVaultsPagedAsync_WithProjectId_IncludesGlobalAndProject()
    {
        _db.Vaults.AddRange(
            new Vault { Name = "Proj", Description = "d", ProjectId = _projectId },
            new Vault { Name = "Global", Description = "d", ProjectId = null },
            new Vault { Name = "Other", Description = "d", ProjectId = _projectId + 100 }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetVaultsPagedAsync(null, _projectId, null, null, 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task GetVaultsPagedAsync_SortByProjectName_OrdersByOwningProject()
    {
        var alpha = new Project { Name = "Alpha" };
        _db.Projects.Add(alpha);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.Vaults.AddRange(
            new Vault { Name = "A", Description = "d", ProjectId = _projectId },   // project "P"
            new Vault { Name = "B", Description = "d", ProjectId = alpha.Id }      // project "Alpha"
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (ascending, _) = await _repo.GetVaultsPagedAsync(
            null, null, null, null, 1, 10, ct: TestContext.Current.CancellationToken, sortBy: "ProjectName");
        Assert.Equal(["B", "A"], ascending.Select(v => v.Name));

        var (descending, _) = await _repo.GetVaultsPagedAsync(
            null, null, null, null, 1, 10, ct: TestContext.Current.CancellationToken, sortBy: "ProjectName", sortDescending: true);
        Assert.Equal(["A", "B"], descending.Select(v => v.Name));
    }

    [Fact]
    public async Task GetVaultsPagedAsync_WithAccessibleIds_Filters()
    {
        _db.Vaults.AddRange(
            new Vault { Name = "V1", Description = "d" },
            new Vault { Name = "V2", Description = "d" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _db.Vaults.Select(v => v.Id).Take(1).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        var (items, total) = await _repo.GetVaultsPagedAsync(null, null, null, null, 1, 10, ids, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetVaultDetailAsync_Found_IncludesSecretsAndVersions()
    {
        var vault = new Vault { Name = "V", Description = "d", ProjectId = _projectId };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secret = new VaultSecret { VaultId = vault.Id, Key = "S1", EncryptedValue = "enc" };
        _db.VaultSecrets.Add(secret);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.VaultSecretVersions.Add(new VaultSecretVersion { VaultSecretId = secret.Id, Version = 1 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetVaultDetailAsync(vault.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Single(result.Secrets);
        Assert.Single(result.Secrets[0].Versions);
    }

    [Fact]
    public async Task GetVaultDetailAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.GetVaultDetailAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindByNamesAsync_ReturnsMatchingVaults()
    {
        _db.Vaults.AddRange(
            new Vault { Name = "V1", Description = "d", ProjectId = _projectId },
            new Vault { Name = "V2", Description = "d", ProjectId = _projectId },
            new Vault { Name = "V3", Description = "d", ProjectId = _projectId + 100 }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindByNamesAsync(["V1", "V2"], _projectId, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetVaultNamesAsync_ReturnsOrderedNamesForProjectAndGlobal()
    {
        _db.Vaults.AddRange(
            new Vault { Name = "Zeta", Description = "d", ProjectId = _projectId },
            new Vault { Name = "Alpha", Description = "d", ProjectId = null },
            new Vault { Name = "Other", Description = "d", ProjectId = _projectId + 100 }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetVaultNamesAsync(_projectId, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Equal("Alpha", result[0]);
    }

    [Fact]
    public async Task FindVaultAsync_Found()
    {
        var vault = new Vault { Name = "V", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindVaultAsync(vault.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task AddVaultAsync_Persists()
    {
        await _repo.AddVaultAsync(new Vault { Name = "New", Description = "d" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Vaults.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveVaultAsync_Removes()
    {
        var vault = new Vault { Name = "Del", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RemoveVaultAsync(vault, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, await _db.Vaults.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindSecretAsync_Found()
    {
        var vault = new Vault { Name = "V", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secret = new VaultSecret { VaultId = vault.Id, Key = "S", EncryptedValue = "enc" };
        _db.VaultSecrets.Add(secret);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindSecretAsync(secret.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task AddSecretAsync_Persists()
    {
        var vault = new Vault { Name = "V", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AddSecretAsync(new VaultSecret { VaultId = vault.Id, Key = "S", EncryptedValue = "enc" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.VaultSecrets.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveSecretAsync_Removes()
    {
        var vault = new Vault { Name = "V", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secret = new VaultSecret { VaultId = vault.Id, Key = "S", EncryptedValue = "enc" };
        _db.VaultSecrets.Add(secret);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RemoveSecretAsync(secret, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, await _db.VaultSecrets.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddSecretVersionAsync_Persists()
    {
        var vault = new Vault { Name = "V", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secret = new VaultSecret { VaultId = vault.Id, Key = "S", EncryptedValue = "enc" };
        _db.VaultSecrets.Add(secret);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AddSecretVersionAsync(new VaultSecretVersion { VaultSecretId = secret.Id, Version = 1 }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.VaultSecretVersions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSecretVersionsAsync_ReturnsOrderedByVersionDesc()
    {
        var vault = new Vault { Name = "V", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secret = new VaultSecret { VaultId = vault.Id, Key = "S", EncryptedValue = "enc" };
        _db.VaultSecrets.Add(secret);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.VaultSecretVersions.AddRange(
            new VaultSecretVersion { VaultSecretId = secret.Id, Version = 1 },
            new VaultSecretVersion { VaultSecretId = secret.Id, Version = 3 },
            new VaultSecretVersion { VaultSecretId = secret.Id, Version = 2 }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetSecretVersionsAsync(secret.Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Count);
        Assert.Equal(3, result[0].Version);
    }

    [Fact]
    public async Task GetNextVersionAsync_ReturnsNextVersion()
    {
        var vault = new Vault { Name = "V", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secret = new VaultSecret { VaultId = vault.Id, Key = "S", EncryptedValue = "enc" };
        _db.VaultSecrets.Add(secret);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.VaultSecretVersions.Add(new VaultSecretVersion { VaultSecretId = secret.Id, Version = 3 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetNextVersionAsync(secret.Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task GetNextVersionAsync_NoVersions_Returns1()
    {
        var vault = new Vault { Name = "V", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secret = new VaultSecret { VaultId = vault.Id, Key = "S", EncryptedValue = "enc" };
        _db.VaultSecrets.Add(secret);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetNextVersionAsync(secret.Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task GetExpiringSecretsAsync_ReturnsOnlyExpiringSoon()
    {
        var vault = new Vault { Name = "V", Description = "d" };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.VaultSecrets.AddRange(
            new VaultSecret { VaultId = vault.Id, Key = "Expiring", EncryptedValue = "e", ExpiresAt = DateTime.UtcNow.AddDays(5) },
            new VaultSecret { VaultId = vault.Id, Key = "NotExpiring", EncryptedValue = "e", ExpiresAt = DateTime.UtcNow.AddDays(60) },
            new VaultSecret { VaultId = vault.Id, Key = "AlreadyExpired", EncryptedValue = "e", ExpiresAt = DateTime.UtcNow.AddDays(-1) },
            new VaultSecret { VaultId = vault.Id, Key = "NoExpiry", EncryptedValue = "e", ExpiresAt = null }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var threshold = DateTime.UtcNow.AddDays(30);
        var result = await _repo.GetExpiringSecretsAsync(threshold, ct: TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("Expiring", result[0].Key);
    }

    [Fact]
    public async Task SaveChangesAsync_PersistsPendingChanges()
    {
        _db.Vaults.Add(new Vault { Name = "Pending", Description = "d" });
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Vaults.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    public void Dispose() => _db.Dispose();
}
