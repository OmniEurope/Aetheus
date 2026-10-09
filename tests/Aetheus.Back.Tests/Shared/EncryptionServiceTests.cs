// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class EncryptionServiceTests
{
    private readonly EncryptionService _sut;

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    public EncryptionServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:EncryptionKey"] = "test-encryption-key-for-tests!"
            })
            .Build();

        _sut = new EncryptionService(config, Env(Environments.Development));
    }

    [Fact]
    public void EncryptValue_ReturnsBase64String()
    {
        var encrypted = _sut.EncryptValue("hello");

        Assert.False(string.IsNullOrEmpty(encrypted));
        Assert.NotEqual("hello", encrypted);
        Convert.FromBase64String(encrypted); // Should not throw
    }

    [Fact]
    public void DecryptValue_RoundTrip_ReturnsOriginal()
    {
        var original = "my-secret-password";
        var encrypted = _sut.EncryptValue(original);
        var decrypted = _sut.DecryptValue(encrypted);

        Assert.Equal(original, decrypted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("a longer value with spaces and special chars !@#$%^&*()")]
    [InlineData("unicode: éàü ñ 日本語")]
    public void EncryptDecrypt_VariousInputs_RoundTrips(string value)
    {
        var encrypted = _sut.EncryptValue(value);
        var decrypted = _sut.DecryptValue(encrypted);

        Assert.Equal(value, decrypted);
    }

    [Fact]
    public void EncryptValue_SameInput_ProducesDifferentCiphertext()
    {
        var encrypted1 = _sut.EncryptValue("same-value");
        var encrypted2 = _sut.EncryptValue("same-value");

        Assert.NotEqual(encrypted1, encrypted2); // Random IV
    }

    [Fact]
    public void DecryptValue_DifferentCiphertexts_SameResult()
    {
        var encrypted1 = _sut.EncryptValue("same-value");
        var encrypted2 = _sut.EncryptValue("same-value");

        Assert.Equal(_sut.DecryptValue(encrypted1), _sut.DecryptValue(encrypted2));
    }

    [Fact]
    public void DecryptValue_WithAnotherKey_IsRejected()
    {
        // Same explicit salt and environment for every instance, so only the configured key differs.
        // Two ciphertexts always differ (random nonce); what proves the key is used is that only
        // the same key opens the value.
        var salt = Convert.ToBase64String(new byte[16]);
        EncryptionService WithKey(string key) => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:EncryptionKey"] = key,
                    ["Auth:EncryptionSalt"] = salt
                })
                .Build(),
            Env(Environments.Production));
        var owner = WithKey("key-a-for-cross-key-test");

        var encrypted = owner.EncryptValue("test");

        Assert.Equal("test", WithKey("key-a-for-cross-key-test").DecryptValue(encrypted));
        Assert.ThrowsAny<CryptographicException>(() => WithKey("key-b-for-cross-key-test").DecryptValue(encrypted));
    }

    [Fact]
    public void Constructor_NoConfigKey_InDevelopment_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var sut = new EncryptionService(config, Env(Environments.Development));

        Assert.Throws<InvalidOperationException>(() => sut.EncryptValue("test"));
    }

    [Fact]
    public void Constructor_NoConfigKey_InProduction_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var sut = new EncryptionService(config, Env(Environments.Production));

        Assert.Throws<InvalidOperationException>(() => sut.EncryptValue("test"));
    }

    [Fact]
    public void DecryptValue_TooShortPayload_Throws()
    {
        var shortPayload = new byte[] { 0x02, 1, 2, 3 };
        var encoded = Convert.ToBase64String(shortPayload);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => _sut.DecryptValue(encoded));
    }

    [Fact]
    public void DecryptValue_LegacyCbc_Rejected_WhenDisabled()
    {
        var fakeCbc = new byte[32];
        var encoded = Convert.ToBase64String(fakeCbc);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => _sut.DecryptValue(encoded));
    }

    [Fact]
    public void DecryptValue_NullOrEmpty_Throws()
    {
        Assert.Throws<ArgumentException>(() => _sut.DecryptValue(""));
        Assert.Throws<ArgumentNullException>(() => _sut.DecryptValue(null!));
    }

    [Fact]
    public void EncryptValue_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.EncryptValue(null!));
    }

    [Fact]
    public void GetConfiguredSalt_Production_NoSalt_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:EncryptionKey"] = "test-key"
            })
            .Build();
        var sut = new EncryptionService(config, Env(Environments.Production));

        Assert.Throws<InvalidOperationException>(() => sut.EncryptValue("test"));
    }

    [Fact]
    public void GetConfiguredSalt_Production_TooShortSalt_Throws()
    {
        var shortSalt = Convert.ToBase64String(new byte[8]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:EncryptionKey"] = "test-key",
                ["Auth:EncryptionSalt"] = shortSalt
            })
            .Build();
        var sut = new EncryptionService(config, Env(Environments.Production));

        Assert.Throws<InvalidOperationException>(() => sut.EncryptValue("test"));
    }

    [Fact]
    public void EncryptDecrypt_WithExplicitSalt_RoundTrips()
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:EncryptionKey"] = "key-with-salt",
                ["Auth:EncryptionSalt"] = salt
            })
            .Build();
        var sut = new EncryptionService(config, Env(Environments.Production));

        var encrypted = sut.EncryptValue("hello-salt");
        var decrypted = sut.DecryptValue(encrypted);
        Assert.Equal("hello-salt", decrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsMultipleValues()
    {
        // Renamed to match the assertion: this verifies two independent encrypt/decrypt round-trips
        // (the key cache is an internal optimisation the test does not - and cannot cheaply - measure).
        var encrypted1 = _sut.EncryptValue("first");
        var encrypted2 = _sut.EncryptValue("second");

        Assert.Equal("first", _sut.DecryptValue(encrypted1));
        Assert.Equal("second", _sut.DecryptValue(encrypted2));
        Assert.NotEqual(encrypted1, encrypted2);
    }
}
