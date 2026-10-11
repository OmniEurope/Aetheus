// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

public class VaultServiceTests
{
    private readonly IVaultRepository _repoMock = Substitute.For<IVaultRepository>();
    private readonly IEncryptionService _encryptionMock = Substitute.For<IEncryptionService>();
    private readonly IDbTransactionScope _transactionMock = Substitute.For<IDbTransactionScope>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly VaultService _sut;

    public VaultServiceTests()
    {
        _sut = new VaultService(_repoMock, _encryptionMock, _transactionMock, _auditMock, Substitute.For<IEntityChangeNotifier>(), TimeProvider.System, Substitute.For<IMemoryCache>());
    }

    [Fact]
    public async Task GetVaultsAsync_ReturnsMappedResult()
    {
        var vaults = new List<Vault>
        {
            new() { Id = 1, Name = "Prod", Description = "Production vault", Secrets = [new() { Id = 1, Key = "K" }] }
        };
        _repoMock.GetVaultsPagedAsync(null, null, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((vaults, 1));

        var result = await _sut.GetVaultsAsync(null, request: new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("Prod", result.Items[0].Name);
        Assert.Equal(1, result.Items[0].SecretCount);
    }

    [Fact]
    public async Task GetVaultsAsync_EmptyList_ReturnsEmptyResult()
    {
        _repoMock.GetVaultsPagedAsync(null, null, null, null, 1, 10, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns((new List<Vault>(), 0));

        var result = await _sut.GetVaultsAsync(null, request: new PaginationRequest { Page = 1, PageSize = 10 }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetVaultDetailAsync_Found_ReturnsDetailDto()
    {
        var vault = new Vault
        {
            Id = 1,
            Name = "Vault1",
            Description = "Desc",
            ProjectId = 2,
            Project = new Project { Id = 2, Name = "MyProject" },
            Secrets = [new() { Id = 10, Key = "DB_PASS" }]
        };
        _repoMock.GetVaultDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(vault);

        var result = await _sut.GetVaultDetailAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Vault1", result.Name);
        Assert.Equal("MyProject", result.ProjectName);
        Assert.Single(result.Secrets);
        Assert.Equal("DB_PASS", result.Secrets[0].Key);
    }

    [Fact]
    public async Task GetVaultDetailAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetVaultDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((Vault?)null);

        var result = await _sut.GetVaultDetailAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetVaultNamesAsync_ReturnsList()
    {
        _repoMock.GetVaultNamesAsync(null, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(["ProdVault", "DevVault"]);

        var result = await _sut.GetVaultNamesAsync(null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Contains("ProdVault", result);
    }

    [Fact]
    public async Task CreateVaultAsync_CallsRepo_ReturnsMappedDto()
    {
        _repoMock.AddVaultAsync(Arg.Any<Vault>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new CreateVaultRequest { Name = "NewVault", Description = "Desc", ProjectId = 3 };
        var result = await _sut.CreateVaultAsync(request, ct: TestContext.Current.CancellationToken);

        Assert.Equal("NewVault", result.Name);
        await _repoMock.Received(1).AddVaultAsync(Arg.Is<Vault>(v => v.Name == "NewVault"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateVaultAsync_Found_UpdatesAndReturns()
    {
        var rowVersion = Guid.NewGuid();
        var vault = new Vault { Id = 1, Name = "Old", Description = "Old desc", RowVersion = rowVersion, Secrets = [] };
        _repoMock.GetVaultDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(vault);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new UpdateVaultRequest { Name = "Updated", Description = "New desc", ProjectId = 5, RowVersion = rowVersion };
        var result = await _sut.UpdateVaultAsync(1, request, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Updated", result.Name);
    }

    [Fact]
    public async Task UpdateVaultAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetVaultDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((Vault?)null);

        var result = await _sut.UpdateVaultAsync(99, new UpdateVaultRequest { Name = "X" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateVaultAsync_ConcurrencyConflict_ThrowsConflictException()
    {
        var vault = new Vault { Id = 1, Name = "Vault", Description = "Desc", Secrets = [] };
        _repoMock.GetVaultDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(vault);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException());

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpdateVaultAsync(1, new UpdateVaultRequest { Name = "Updated" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteVaultAsync_Found_ReturnsTrue()
    {
        var vault = new Vault { Id = 1, Name = "ToDelete" };
        _repoMock.FindVaultAsync(1, Arg.Any<CancellationToken>())
            .Returns(vault);
        _repoMock.RemoveVaultAsync(vault, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.DeleteVaultAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task DeleteVaultAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindVaultAsync(99, Arg.Any<CancellationToken>())
            .Returns((Vault?)null);

        var result = await _sut.DeleteVaultAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CreateSecretAsync_VaultFound_EncryptsAndCreatesVersion()
    {
        var vault = new Vault { Id = 1, Name = "V" };
        _repoMock.FindVaultAsync(1, Arg.Any<CancellationToken>()).Returns(vault);
        _encryptionMock.EncryptValue("secret123").Returns("encrypted-base64");
        _repoMock.AddSecretAsync(Arg.Any<VaultSecret>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetNextVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        _repoMock.AddSecretVersionAsync(Arg.Any<VaultSecretVersion>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateSecretAsync(1, new CreateVaultSecretRequest { Key = "DB_PASS", Value = "secret123" }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("DB_PASS", result.Key);
        _encryptionMock.Received(1).EncryptValue("secret123");
        await _repoMock.Received(1).AddSecretAsync(
            Arg.Is<VaultSecret>(s => s.EncryptedValue == "encrypted-base64"),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AddSecretVersionAsync(
            Arg.Is<VaultSecretVersion>(v => v.ChangeType == ChangeType.Created),
            Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateSecretAsync_VaultNotFound_ThrowsNotFoundException()
    {
        _repoMock.FindVaultAsync(99, Arg.Any<CancellationToken>())
            .Returns((Vault?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.CreateSecretAsync(99, new CreateVaultSecretRequest { Key = "K", Value = "V" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateSecretAsync_Found_EncryptsAndCreatesVersion()
    {
        var secret = new VaultSecret { Id = 10, VaultId = 1, Key = "OLD", EncryptedValue = "old-enc" };
        _repoMock.FindSecretAsync(10, Arg.Any<CancellationToken>()).Returns(secret);
        _encryptionMock.EncryptValue("new-secret").Returns("new-enc");
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetNextVersionAsync(10, Arg.Any<CancellationToken>()).Returns(2);
        _repoMock.AddSecretVersionAsync(Arg.Any<VaultSecretVersion>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.UpdateSecretAsync(1, 10, new UpdateVaultSecretRequest { Key = "NEW", Value = "new-secret" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("NEW", result.Key);
        await _repoMock.Received(1).AddSecretVersionAsync(
            Arg.Is<VaultSecretVersion>(v => v.ChangeType == ChangeType.Updated),
            Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSecretAsync_HistoryKeepsTheNameAndAuthor_NeverTheValue()
    {
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "alice")], "test"))
            }
        };
        var sut = new VaultService(_repoMock, _encryptionMock, _transactionMock, _auditMock,
            Substitute.For<IEntityChangeNotifier>(), TimeProvider.System, Substitute.For<IMemoryCache>(), accessor);
        _repoMock.FindSecretAsync(10, Arg.Any<CancellationToken>())
            .Returns(new VaultSecret { Id = 10, VaultId = 1, Key = "OLD", EncryptedValue = "old-enc" });
        _encryptionMock.EncryptValue("new-secret").Returns("new-enc");
        _repoMock.GetNextVersionAsync(10, Arg.Any<CancellationToken>()).Returns(2);
        VaultSecretVersion? written = null;
        await _repoMock.AddSecretVersionAsync(Arg.Do<VaultSecretVersion>(v => written = v), Arg.Any<CancellationToken>());

        await sut.UpdateSecretAsync(1, 10, new UpdateVaultSecretRequest { Key = "NEW", Value = "new-secret" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(written);
        Assert.Equal("NEW", written.Key);
        Assert.Equal("alice", written.ChangedBy);
        // The history entity no longer carries a value at all (R-287 contract step 1).
        Assert.Null(typeof(VaultSecretVersion).GetProperty("EncryptedValue"));
        Assert.True(written.ChangedAt.Year > 2000);
    }

    [Fact]
    public async Task UpdateSecretAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindSecretAsync(99, Arg.Any<CancellationToken>())
            .Returns((VaultSecret?)null);

        var result = await _sut.UpdateSecretAsync(1, 99, new UpdateVaultSecretRequest { Key = "K", Value = "V" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateSecretAsync_WrongVaultId_ReturnsNull()
    {
        var secret = new VaultSecret { Id = 10, VaultId = 2, Key = "K" };
        _repoMock.FindSecretAsync(10, Arg.Any<CancellationToken>()).Returns(secret);

        var result = await _sut.UpdateSecretAsync(1, 10, new UpdateVaultSecretRequest { Key = "K", Value = "V" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteSecretAsync_Found_CreatesVersionAndRemoves()
    {
        var secret = new VaultSecret { Id = 10, VaultId = 1, Key = "K", EncryptedValue = "enc" };
        _repoMock.FindSecretAsync(10, Arg.Any<CancellationToken>()).Returns(secret);
        _repoMock.GetNextVersionAsync(10, Arg.Any<CancellationToken>()).Returns(3);
        _repoMock.AddSecretVersionAsync(Arg.Any<VaultSecretVersion>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.RemoveSecretAsync(secret, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.DeleteSecretAsync(1, 10, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).AddSecretVersionAsync(
            Arg.Is<VaultSecretVersion>(v => v.ChangeType == ChangeType.Deleted),
            Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteSecretAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindSecretAsync(99, Arg.Any<CancellationToken>())
            .Returns((VaultSecret?)null);

        var result = await _sut.DeleteSecretAsync(1, 99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task GetSecretVersionsAsync_Found_ReturnsMappedVersions()
    {
        var secret = new VaultSecret { Id = 10, VaultId = 1, Key = "K" };
        _repoMock.FindSecretAsync(10, Arg.Any<CancellationToken>()).Returns(secret);
        _repoMock.GetSecretVersionsAsync(10, Arg.Any<CancellationToken>())
            .Returns([
                new VaultSecretVersion { Version = 1, Key = "K", ChangeType = ChangeType.Created },
                new VaultSecretVersion { Version = 2, Key = "K2", ChangeType = ChangeType.Updated }
            ]);

        var result = await _sut.GetSecretVersionsAsync(1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(ChangeType.Created, result[0].ChangeType);
    }

    [Fact]
    public async Task GetSecretVersionsAsync_SecretNotFound_ThrowsNotFoundException()
    {
        _repoMock.FindSecretAsync(99, Arg.Any<CancellationToken>())
            .Returns((VaultSecret?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.GetSecretVersionsAsync(1, 99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveVaultSecretsAsync_MergesGlobalAndProject_Decrypts()
    {
        var vaults = new List<Vault>
        {
            new()
            {
                Id = 1, Name = "Secrets", ProjectId = null,
                Secrets = [
                    new() { Key = "DB_PASS", EncryptedValue = "enc-global-db" },
                    new() { Key = "API_KEY", EncryptedValue = "enc-api" }
                ]
            },
            new()
            {
                Id = 2, Name = "Secrets", ProjectId = 5,
                Secrets = [
                    new() { Key = "DB_PASS", EncryptedValue = "enc-project-db" }
                ]
            }
        };
        _repoMock.FindByNamesAsync(Arg.Any<List<string>>(), 5, Arg.Any<CancellationToken>())
            .Returns(vaults);
        _encryptionMock.DecryptValue("enc-global-db").Returns("global-db-pass");
        _encryptionMock.DecryptValue("enc-api").Returns("api-key-val");
        _encryptionMock.DecryptValue("enc-project-db").Returns("project-db-pass");

        var result = await _sut.ResolveVaultSecretsAsync(["Secrets"], 5, ct: TestContext.Current.CancellationToken);

        Assert.Equal("project-db-pass", result["DB_PASS"]); // Project overrides global
        Assert.Equal("api-key-val", result["API_KEY"]); // Global kept
    }

    [Fact]
    public async Task ResolveVaultSecretsAsync_NoVaults_ReturnsEmptyDictionary()
    {
        _repoMock.FindByNamesAsync(Arg.Any<List<string>>(), null, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _sut.ResolveVaultSecretsAsync(["NonExistent"], null, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    // --- ExportSecretKeysAsync ---

    [Fact]
    public async Task ExportSecretKeysAsync_ReturnsKeyList()
    {
        _repoMock.GetVaultDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Vault
            {
                Id = 1,
                Name = "v",
                Secrets = [
                    new VaultSecret { Key = "KEY1" },
                    new VaultSecret { Key = "KEY2" }
                ]
            });

        var result = await _sut.ExportSecretKeysAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Contains("KEY1", result);
    }

    [Fact]
    public async Task ExportSecretKeysAsync_NotFound_ThrowsNotFoundException()
    {
        _repoMock.GetVaultDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((Vault?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ExportSecretKeysAsync(99, ct: TestContext.Current.CancellationToken));
    }

    // --- ImportSecretsAsync ---

    [Fact]
    public async Task ImportSecretsAsync_CreatesSecretsAndVersions()
    {
        _repoMock.FindVaultAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Vault { Id = 1, Name = "v" });
        _encryptionMock.EncryptValue(Arg.Any<string>()).Returns("encrypted");
        _repoMock.GetNextVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);

        var secrets = new List<CreateVaultSecretRequest>
        {
            new() { Key = "K1", Value = "V1" },
            new() { Key = "K2", Value = "V2" }
        };

        var count = await _sut.ImportSecretsAsync(1, secrets, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        await _repoMock.Received(1).AddSecretsRangeAsync(Arg.Is<List<VaultSecret>>(l => l.Count == 2), Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AddSecretVersionsRangeAsync(Arg.Is<List<VaultSecretVersion>>(l => l.Count == 2), Arg.Any<CancellationToken>());
        await _transactionMock.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportSecretsAsync_VaultNotFound_ThrowsNotFoundException()
    {
        _repoMock.FindVaultAsync(99, Arg.Any<CancellationToken>())
            .Returns((Vault?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ImportSecretsAsync(99, [], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportSecretKeysAsync_Found_ReturnsKeys()
    {
        _repoMock.GetVaultDetailAsync(1, TestContext.Current.CancellationToken)
            .Returns(new Vault
            {
                Id = 1,
                Name = "v",
                Secrets = [new VaultSecret { Key = "DB_PASS" }, new VaultSecret { Key = "API_KEY" }]
            });

        var result = await _sut.ExportSecretKeysAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Contains("DB_PASS", result);
    }

    [Fact]
    public async Task ExportSecretKeysAsync_NotFound_Throws()
    {
        _repoMock.GetVaultDetailAsync(99, TestContext.Current.CancellationToken)
            .Returns((Vault?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ExportSecretKeysAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportSecretsAsync_Success_CreatesSecretsAndVersions()
    {
        _repoMock.FindVaultAsync(1, TestContext.Current.CancellationToken)
            .Returns(new Vault { Id = 1, Name = "v" });
        _transactionMock.BeginTransactionAsync(TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _transactionMock.CommitAsync(TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _encryptionMock.EncryptValue("val1").Returns("enc1");
        _repoMock.AddSecretAsync(Arg.Any<VaultSecret>(), TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _repoMock.GetNextVersionAsync(Arg.Any<int>(), TestContext.Current.CancellationToken).Returns(1);
        _repoMock.AddSecretVersionAsync(Arg.Any<VaultSecretVersion>(), TestContext.Current.CancellationToken).Returns(Task.CompletedTask);

        var result = await _sut.ImportSecretsAsync(1, [new CreateVaultSecretRequest { Key = "K", Value = "val1" }], ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        await _transactionMock.Received(1).CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ImportSecretsAsync_NotFound_Throws()
    {
        _repoMock.FindVaultAsync(99, TestContext.Current.CancellationToken)
            .Returns((Vault?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.ImportSecretsAsync(99, [new CreateVaultSecretRequest { Key = "K", Value = "V" }], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveVaultSecretsAsync_MergesGlobalAndProject()
    {
        _repoMock.FindByNamesAsync(Arg.Any<List<string>>(), 1, TestContext.Current.CancellationToken)
            .Returns([
                new Vault { Id = 1, Name = "secrets", ProjectId = null, Secrets = [new VaultSecret { Key = "A", EncryptedValue = "encG" }, new VaultSecret { Key = "B", EncryptedValue = "encB" }] },
                new Vault { Id = 2, Name = "secrets", ProjectId = 1, Secrets = [new VaultSecret { Key = "A", EncryptedValue = "encP" }] }
            ]);
        _encryptionMock.DecryptValue("encG").Returns("global");
        _encryptionMock.DecryptValue("encB").Returns("globalB");
        _encryptionMock.DecryptValue("encP").Returns("project");

        var result = await _sut.ResolveVaultSecretsAsync(["secrets"], 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("project", result["A"]);
        Assert.Equal("globalB", result["B"]);
    }

    [Fact]
    public async Task DeleteSecretAsync_Found_DeletesAndReturnsTrue()
    {
        var secret = new VaultSecret { Id = 1, VaultId = 5, Key = "K", EncryptedValue = "enc" };
        _repoMock.FindSecretAsync(1, TestContext.Current.CancellationToken).Returns(secret);
        _transactionMock.BeginTransactionAsync(TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _transactionMock.CommitAsync(TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _repoMock.GetNextVersionAsync(1, TestContext.Current.CancellationToken).Returns(2);
        _repoMock.AddSecretVersionAsync(Arg.Any<VaultSecretVersion>(), TestContext.Current.CancellationToken).Returns(Task.CompletedTask);
        _repoMock.RemoveSecretAsync(secret, TestContext.Current.CancellationToken).Returns(Task.CompletedTask);

        var result = await _sut.DeleteSecretAsync(5, 1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _transactionMock.Received(1).CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteSecretAsync_SecretNull_ReturnsFalse()
    {
        _repoMock.FindSecretAsync(1, TestContext.Current.CancellationToken).Returns((VaultSecret?)null);

        var result = await _sut.DeleteSecretAsync(5, 1, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteSecretAsync_WrongVault_ReturnsFalse()
    {
        _repoMock.FindSecretAsync(1, TestContext.Current.CancellationToken)
            .Returns(new VaultSecret { Id = 1, VaultId = 99, Key = "K", EncryptedValue = "e" });

        var result = await _sut.DeleteSecretAsync(5, 1, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task GetSecretVersionsAsync_ReturnsVersionList()
    {
        var secret = new VaultSecret { Id = 1, VaultId = 5, Key = "K", EncryptedValue = "e" };
        _repoMock.FindSecretAsync(1, TestContext.Current.CancellationToken).Returns(secret);
        _repoMock.GetSecretVersionsAsync(1, TestContext.Current.CancellationToken)
            .Returns([new VaultSecretVersion { Version = 1, Key = "K", ChangedAt = DateTime.UtcNow, ChangeType = ChangeType.Created }]);

        var result = await _sut.GetSecretVersionsAsync(5, 1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(ChangeType.Created, result[0].ChangeType);
    }

    [Fact]
    public async Task GetSecretVersionsAsync_SecretNull_Throws()
    {
        _repoMock.FindSecretAsync(1, TestContext.Current.CancellationToken).Returns((VaultSecret?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetSecretVersionsAsync(5, 1, ct: TestContext.Current.CancellationToken));
    }
}

