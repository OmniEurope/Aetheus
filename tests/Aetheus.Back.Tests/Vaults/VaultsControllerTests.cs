// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class VaultsControllerTests
{
    private readonly IVaultService _serviceMock = Substitute.For<IVaultService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly VaultsController _sut;

    public VaultsControllerTests()
    {
        // ADR-059: the caller's organization lets it give a vault to the project (the denial has its own test).
        _authzMock.CanOwnAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new VaultsController(_serviceMock, _authzMock);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    [Fact]
    public async Task GetVaults_ReturnsOk()
    {
        _authzMock.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1 });
        _serviceMock.GetVaultsAsync(null, null, null, Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto> { Items = [new VaultDto { Id = 1, Name = "Vault1" }], TotalCount = 1 });

        var result = await _sut.GetVaults(null, null, null, new PaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetVault_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetVaultDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new VaultDetailDto { Id = 1, Name = "Vault1" });

        var result = await _sut.GetVault(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetVault_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetVault(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetVaultNames_ReturnsOk()
    {
        _serviceMock.GetVaultNamesAsync(null, Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(["Vault1"]);

        var result = await _sut.GetVaultNames(null, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateVault_Authorized_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreateVaultAsync(Arg.Any<CreateVaultRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VaultDto { Id = 1, Name = "New" });

        var result = await _sut.CreateVault(new CreateVaultRequest { Name = "New" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreateVault_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CreateVault(new CreateVaultRequest { Name = "X" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdateVault_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.UpdateVaultAsync(1, Arg.Any<UpdateVaultRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VaultDto { Id = 1, Name = "Updated" });

        var result = await _sut.UpdateVault(1, new UpdateVaultRequest { Name = "Updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DeleteVault_Authorized_ReturnsNoContent()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.DeleteVaultAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteVault(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteVault_Forbidden_ReturnsForbid()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.DeleteVault(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task CreateSecret_Authorized_ReturnsCreated()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.CreateSecretAsync(1, Arg.Any<CreateVaultSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VaultSecretDto { Id = 1, Key = "SECRET" });

        var result = await _sut.CreateSecret(1, new CreateVaultSecretRequest { Key = "SECRET", Value = "val" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedResult>(result.Result);
    }

    [Fact]
    public async Task UpdateSecret_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.UpdateSecretAsync(1, 2, Arg.Any<UpdateVaultSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VaultSecretDto { Id = 2, Key = "Updated" });

        var result = await _sut.UpdateSecret(1, 2, new UpdateVaultSecretRequest { Key = "Updated", Value = "v2" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DeleteSecret_Authorized_ReturnsNoContent()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.DeleteSecretAsync(1, 2, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteSecret(1, 2, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task GetSecretVersions_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.GetSecretVersionsAsync(1, 2, Arg.Any<CancellationToken>())
            .Returns([new VaultSecretVersionDto { Version = 1 }]);

        var result = await _sut.GetSecretVersions(1, 2, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task ExportSecretKeys_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.ExportSecretKeysAsync(1, Arg.Any<CancellationToken>())
            .Returns(["KEY1", "KEY2"]);

        var result = await _sut.ExportSecretKeys(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task ImportSecrets_Authorized_ReturnsOk()
    {
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Vault, 1, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _serviceMock.ImportSecretsAsync(1, Arg.Any<List<CreateVaultSecretRequest>>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _sut.ImportSecrets(1, [new CreateVaultSecretRequest { Key = "K", Value = "V" }], TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var importResult = Assert.IsType<ImportResultDto>(ok.Value);
        Assert.Equal(2, importResult.ImportedCount);
    }
}

