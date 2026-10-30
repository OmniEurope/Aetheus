// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Copying a secret's value reads it once, audited, never cached, Write only.</summary>
public class VaultSecretRevealTests
{
    private readonly IVaultRepository _repo = Substitute.For<IVaultRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly VaultService _service;

    public VaultSecretRevealTests() =>
        _service = new VaultService(_repo, _encryption, Substitute.For<IDbTransactionScope>(), _audit,
            Substitute.For<IEntityChangeNotifier>(), TimeProvider.System, Substitute.For<IMemoryCache>());

    [Fact]
    public async Task Reveal_ReturnsTheClearValue_AndAuditsTheKeyNotTheValue()
    {
        _repo.FindSecretAsync(5, Arg.Any<CancellationToken>())
            .Returns(new VaultSecret { Id = 5, VaultId = 1, Key = "DB_PASSWORD", EncryptedValue = "enc" });
        _encryption.DecryptValue("enc").Returns("s3cret");

        var value = await _service.RevealSecretValueAsync(1, 5, TestContext.Current.CancellationToken);

        Assert.Equal("s3cret", value);
        await _audit.Received(1).LogAsync("RevealedSecret", "VaultSecret", 5, "DB_PASSWORD", Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Is<string>(detail => detail.Contains("s3cret")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reveal_SecretOfAnotherVault_ReturnsNull_WithoutDecrypting()
    {
        _repo.FindSecretAsync(5, Arg.Any<CancellationToken>())
            .Returns(new VaultSecret { Id = 5, VaultId = 2, Key = "DB_PASSWORD", EncryptedValue = "enc" });

        Assert.Null(await _service.RevealSecretValueAsync(1, 5, TestContext.Current.CancellationToken));
        _encryption.DidNotReceive().DecryptValue(Arg.Any<string>());
    }

    [Fact]
    public async Task Endpoint_WithoutWritePermission_IsForbidden_AndReadsNothing()
    {
        var service = Substitute.For<IVaultService>();
        var authz = Substitute.For<IResourceAuthorizationService>();
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.VaultSecretReveal, 1, Arg.Any<CancellationToken>())
            .Returns(false);
        var controller = Controller(service, authz);

        var result = await controller.RevealSecretValue(1, 5, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await service.DidNotReceive().RevealSecretValueAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Endpoint_Allowed_ReturnsTheValue_WithNoStore()
    {
        var service = Substitute.For<IVaultService>();
        service.RevealSecretValueAsync(1, 5, Arg.Any<CancellationToken>()).Returns("s3cret");
        var authz = Substitute.For<IResourceAuthorizationService>();
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.VaultSecretReveal, 1, Arg.Any<CancellationToken>())
            .Returns(true);
        var controller = Controller(service, authz);

        var result = await controller.RevealSecretValue(1, 5, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("s3cret", Assert.IsType<RevealedSecretValueDto>(ok.Value).Value);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    private static VaultsController Controller(IVaultService service, IResourceAuthorizationService authz) =>
        new(service, authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
                }
            }
        };
}
