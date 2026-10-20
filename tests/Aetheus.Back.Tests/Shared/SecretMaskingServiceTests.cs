// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class SecretMaskingServiceTests
{
    private readonly ISecretMaskingRepository _repoMock = Substitute.For<ISecretMaskingRepository>();
    private readonly IEncryptionService _encryptionMock = Substitute.For<IEncryptionService>();
    private readonly SecretMaskingService _sut;

    public SecretMaskingServiceTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        _sut = new SecretMaskingService(_repoMock, _encryptionMock, cache);
    }

    [Fact]
    public async Task MaskAsync_NullMessage_ReturnsMessage()
    {
        var result = await _sut.MaskAsync(null!, 1, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task MaskAsync_EmptyMessage_ReturnsEmpty()
    {
        var result = await _sut.MaskAsync("", 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task MaskAsync_NullPipelineRunId_ReturnsUnchanged()
    {
        var result = await _sut.MaskAsync("some log message", null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("some log message", result);
    }

    [Fact]
    public async Task MaskAsync_NoMatchingRun_ReturnsUnchanged()
    {
        _repoMock.GetPipelineIdForRunAsync(999, Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await _sut.MaskAsync("some log message", 999, ct: TestContext.Current.CancellationToken);

        Assert.Equal("some log message", result);
    }

    [Fact]
    public async Task MaskAsync_WithSecrets_MasksValues()
    {
        _repoMock.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>())
            .Returns(1);
        _repoMock.GetPipelineYamlAndProjectAsync(1, Arg.Any<CancellationToken>())
            .Returns(("vaults:\n- MyVault", (int?)null));
        _repoMock.GetEncryptedSecretsAsync(Arg.Is<List<string>>(l => l.Contains("MyVault")), null, Arg.Any<CancellationToken>())
            .Returns(["enc-pass"]);
        _encryptionMock.DecryptValue("enc-pass").Returns("SuperSecret123");

        var result = await _sut.MaskAsync("Connecting with SuperSecret123 to db", 1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Connecting with *** to db", result);
        Assert.DoesNotContain("SuperSecret123", result);
    }

    [Fact]
    public async Task MaskAsync_NoVaultsInYaml_ReturnsUnchanged()
    {
        _repoMock.GetPipelineIdForRunAsync(2, Arg.Any<CancellationToken>())
            .Returns(2);
        _repoMock.GetPipelineYamlAndProjectAsync(2, Arg.Any<CancellationToken>())
            .Returns(("name: test\nstages: []", (int?)null));

        var result = await _sut.MaskAsync("some log message", 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal("some log message", result);
    }

    [Fact]
    public async Task MaskAsync_CachesSecrets_SecondCallUsesCache()
    {
        _repoMock.GetPipelineIdForRunAsync(3, Arg.Any<CancellationToken>())
            .Returns(3);
        _repoMock.GetPipelineYamlAndProjectAsync(3, Arg.Any<CancellationToken>())
            .Returns(("vaults:\n- CachedVault", (int?)null));
        _repoMock.GetEncryptedSecretsAsync(Arg.Any<List<string>>(), null, Arg.Any<CancellationToken>())
            .Returns(["enc"]);
        _encryptionMock.DecryptValue("enc").Returns("password");

        await _sut.MaskAsync("password here", 3, ct: TestContext.Current.CancellationToken);
        await _sut.MaskAsync("password again", 3, ct: TestContext.Current.CancellationToken);

        // Decryption called only once (cached)
        _encryptionMock.Received(1).DecryptValue("enc");
    }

    [Fact]
    public async Task EvictCache_RemovesCachedSecrets()
    {
        _repoMock.GetPipelineIdForRunAsync(4, Arg.Any<CancellationToken>())
            .Returns(4);
        _repoMock.GetPipelineYamlAndProjectAsync(4, Arg.Any<CancellationToken>())
            .Returns(("vaults:\n- EvictVault", (int?)null));
        _repoMock.GetEncryptedSecretsAsync(Arg.Any<List<string>>(), null, Arg.Any<CancellationToken>())
            .Returns(["enc2"]);
        _encryptionMock.DecryptValue("enc2").Returns("secret");

        await _sut.MaskAsync("secret in log", 4, ct: TestContext.Current.CancellationToken);

        _sut.EvictCache(4);

        await _sut.MaskAsync("secret again", 4, ct: TestContext.Current.CancellationToken);

        // Decrypted twice: once before eviction, once after
        _encryptionMock.Received(2).DecryptValue("enc2");
    }

    [Fact]
    public async Task MaskAsync_MultipleSecrets_MasksLongestFirst()
    {
        _repoMock.GetPipelineIdForRunAsync(5, Arg.Any<CancellationToken>())
            .Returns(5);
        _repoMock.GetPipelineYamlAndProjectAsync(5, Arg.Any<CancellationToken>())
            .Returns(("vaults:\n- MultiVault", (int?)null));
        _repoMock.GetEncryptedSecretsAsync(Arg.Any<List<string>>(), null, Arg.Any<CancellationToken>())
            .Returns(["enc-short", "enc-long"]);
        _encryptionMock.DecryptValue("enc-short").Returns("abcd");
        _encryptionMock.DecryptValue("enc-long").Returns("abcdef");

        var result = await _sut.MaskAsync("Value is abcdef and abcd", 5, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Value is *** and ***", result);
    }

    [Fact]
    public async Task MaskAsync_ProjectScopedVault_IncludesSecrets()
    {
        _repoMock.GetPipelineIdForRunAsync(6, Arg.Any<CancellationToken>())
            .Returns(6);
        _repoMock.GetPipelineYamlAndProjectAsync(6, Arg.Any<CancellationToken>())
            .Returns(("vaults:\n- ProjVault", 10));
        _repoMock.GetEncryptedSecretsAsync(Arg.Is<List<string>>(l => l.Contains("ProjVault")), 10, Arg.Any<CancellationToken>())
            .Returns(["enc-proj"]);
        _encryptionMock.DecryptValue("enc-proj").Returns("proj-secret");

        var result = await _sut.MaskAsync("Log: proj-secret here", 6, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Log: *** here", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void RegisterRuntimeSecret_TooShortToMask_IsIgnored(string secret)
    {
        _sut.RegisterRuntimeSecret(7, secret);

        Assert.Empty(_sut.GetRuntimeSecretValues(7));
    }

    [Fact]
    public void RegisterRuntimeSecret_KeepsEachValueOnce_LongestFirst_PerRun()
    {
        _sut.RegisterRuntimeSecret(7, "token");
        _sut.RegisterRuntimeSecret(7, "token-with-suffix");
        _sut.RegisterRuntimeSecret(7, "token");
        _sut.RegisterRuntimeSecret(8, "other-run");

        Assert.Equal(["token-with-suffix", "token"], _sut.GetRuntimeSecretValues(7));
        Assert.Equal(["other-run"], _sut.GetRuntimeSecretValues(8));
    }
}
