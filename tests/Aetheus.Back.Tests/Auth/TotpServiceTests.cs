// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using NSubstitute;
using OtpNet;

namespace Aetheus.Back.Tests;

public class TotpServiceTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly IAuthRepository _repoMock = Substitute.For<IAuthRepository>();
    private readonly IEncryptionService _encryptionMock = Substitute.For<IEncryptionService>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly TimeProvider _timeProvider = Substitute.For<TimeProvider>();
    private readonly TotpService _sut;

    public TotpServiceTests()
    {
        _timeProvider.GetUtcNow().Returns(FixedNow);

        // Encryption round-trips: encrypt returns "enc:" prefix, decrypt strips it.
        _encryptionMock.EncryptValue(Arg.Any<string>()).Returns(ci => $"enc:{ci.Arg<string>()}");
        _encryptionMock.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>().Replace("enc:", ""));

        var jwtOptions = new JwtOptions { SigningKey = "aetheus-dev-key-minimum-32-bytes!!" };
        _sut = new TotpService(_repoMock, _encryptionMock, _auditMock, jwtOptions, _timeProvider);
    }

    // --- SetupTotpAsync ---

    [Fact]
    public async Task SetupTotpAsync_UserNotFound_ThrowsNotFoundException()
    {
        _repoMock.FindUserByIdTrackedAsync(99, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.SetupTotpAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetupTotpAsync_TotpAlreadyEnabled_ThrowsConflictException()
    {
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "admin", TotpEnabled = true });

        await Assert.ThrowsAsync<ConflictException>(() => _sut.SetupTotpAsync(1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetupTotpAsync_ValidUser_ReturnsSetupResponse()
    {
        var user = new User { Id = 1, Username = "admin", TotpEnabled = false };
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.SetupTotpAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.SharedKey));
        Assert.Contains("otpauth://totp/", result.AuthenticatorUri);
        Assert.Contains("admin", result.AuthenticatorUri);
        Assert.Equal(8, result.RecoveryCodes.Count);
        Assert.All(result.RecoveryCodes, code => Assert.Contains("-", code));
        await _repoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetupTotpAsync_SetsEncryptedSecretOnUser()
    {
        var user = new User { Id = 1, Username = "admin", TotpEnabled = false };
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        await _sut.SetupTotpAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(user.TotpSecret);
        Assert.StartsWith("enc:", user.TotpSecret);
        Assert.NotNull(user.TotpRecoveryCodes);
    }

    // --- VerifyAndEnableTotpAsync ---

    [Fact]
    public async Task VerifyAndEnableTotpAsync_UserNotFound_ReturnsFalse()
    {
        _repoMock.FindUserByIdTrackedAsync(99, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _sut.VerifyAndEnableTotpAsync(99, "123456", ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task VerifyAndEnableTotpAsync_NoSecret_ReturnsFalse()
    {
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, TotpSecret = null });

        var result = await _sut.VerifyAndEnableTotpAsync(1, "123456", ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task VerifyAndEnableTotpAsync_AlreadyEnabled_ReturnsTrue()
    {
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, TotpSecret = "enc:JBSWY3DPEHPK3PXP", TotpEnabled = true });

        var result = await _sut.VerifyAndEnableTotpAsync(1, "123456", ct: TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task VerifyAndEnableTotpAsync_ValidCode_EnablesAndReturnsTrue()
    {
        // Generate a real TOTP code to validate
        var secret = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(secret);
        var totp = new Totp(secret, step: 30, totpSize: 6);
        var code = totp.ComputeTotp(FixedNow.UtcDateTime);

        var user = new User { Id = 1, Username = "admin", TotpSecret = $"enc:{base32Secret}", TotpEnabled = false };
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.VerifyAndEnableTotpAsync(1, code, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.True(user.TotpEnabled);
        await _auditMock.Received(1).LogAsync("TotpEnabled", "User", 1, "admin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAndEnableTotpAsync_InvalidCode_ReturnsFalse()
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(secret);

        var user = new User { Id = 1, Username = "admin", TotpSecret = $"enc:{base32Secret}", TotpEnabled = false };
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.VerifyAndEnableTotpAsync(1, "000000", ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.False(user.TotpEnabled);
    }

    // --- DisableTotpAsync ---

    [Fact]
    public async Task DisableTotpAsync_UserNotFound_ReturnsFalse()
    {
        _repoMock.FindUserByIdTrackedAsync(99, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _sut.DisableTotpAsync(99, Disable("password", "123456"), ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DisableTotpAsync_TotpNotEnabled_ReturnsFalse()
    {
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, TotpEnabled = false });

        var result = await _sut.DisableTotpAsync(1, Disable("password", "123456"), ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DisableTotpAsync_WrongPassword_ReturnsFalse()
    {
        var (user, code) = TotpUser("correctpassword");
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.DisableTotpAsync(1, Disable("wrongpassword", code), ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.True(user.TotpEnabled);
        // R-50: a failure counts against the sign-in lockout.
        await _repoMock.Received(1).IncrementFailedLoginAsync(1, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisableTotpAsync_CorrectPassword_ClearsAndReturnsTrue()
    {
        var (user, code) = TotpUser("mypassword");
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.DisableTotpAsync(1, Disable("mypassword", code), ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Null(user.TotpSecret);
        Assert.False(user.TotpEnabled);
        Assert.Null(user.TotpRecoveryCodes);
        await _repoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("TotpDisabled", "User", 1, "admin", Arg.Any<CancellationToken>());
    }

    // --- DisableTotpAsync, R-50: password AND second factor, sign-in lockout, stamp rotation ---

    [Fact]
    public async Task DisableTotpAsync_PasswordWithoutCode_RefusedAndCounted()
    {
        var (user, _) = TotpUser("mypassword");
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.DisableTotpAsync(1, Disable("mypassword", ""), ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.True(user.TotpEnabled);
        Assert.NotNull(user.TotpSecret);
        await _repoMock.Received(1).IncrementFailedLoginAsync(1, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisableTotpAsync_WrongTotpCode_RefusedAndCounted()
    {
        var (user, code) = TotpUser("mypassword");
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        var wrong = code == "000000" ? "111111" : "000000";

        var result = await _sut.DisableTotpAsync(1, Disable("mypassword", wrong), ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.True(user.TotpEnabled);
        Assert.Equal("stamp-before", user.SecurityStamp);
        await _repoMock.Received(1).IncrementFailedLoginAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisableTotpAsync_FifthFailure_LocksTheAccountLikeTheSignIn()
    {
        var (user, _) = TotpUser("mypassword");
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        var counts = 0;
        _repoMock.IncrementFailedLoginAsync(1, Arg.Any<CancellationToken>()).Returns(_ => ++counts);

        for (var attempt = 0; attempt < 5; attempt++)
            Assert.False(await _sut.DisableTotpAsync(1, Disable("wrong", "123456"), ct: TestContext.Current.CancellationToken));

        // The sign-in lockout: 15 minutes after the fifth failure, only once the threshold is reached.
        await _repoMock.Received(1).LockoutUserAsync(1, FixedNow.UtcDateTime.AddMinutes(15), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisableTotpAsync_LockedAccount_RefusedEvenWithTheRightPasswordAndCode()
    {
        var (user, code) = TotpUser("mypassword");
        user.LockoutEndUtc = FixedNow.UtcDateTime.AddMinutes(5);
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.DisableTotpAsync(1, Disable("mypassword", code), ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.True(user.TotpEnabled);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisableTotpAsync_ValidTotp_RotatesTheStampAndClearsTheFailures()
    {
        var (user, code) = TotpUser("mypassword");
        user.FailedLoginCount = 3;
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.DisableTotpAsync(1, Disable("mypassword", code), ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.False(user.TotpEnabled);
        Assert.NotEqual("stamp-before", user.SecurityStamp);
        Assert.False(string.IsNullOrEmpty(user.SecurityStamp));
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEndUtc);
    }

    [Fact]
    public async Task DisableTotpAsync_RecoveryCode_ConsumedAndAccepted()
    {
        var (user, _) = TotpUser("mypassword");
        var stored = JsonSerializer.Serialize(new List<string> { BCrypt.Net.BCrypt.HashPassword("ABCDE12345") });
        user.TotpRecoveryCodes = stored;
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        _repoMock.ReplaceTotpRecoveryCodesAsync(1, stored, "[]", Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DisableTotpAsync(1, Disable("mypassword", "ABCDE-12345", recovery: true), ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.False(user.TotpEnabled);
        Assert.NotEqual("stamp-before", user.SecurityStamp);
        await _repoMock.Received(1).ReplaceTotpRecoveryCodesAsync(1, stored, "[]", Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("RecoveryCodeUsed", "User", 1, "admin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisableTotpAsync_WrongPasswordWithRecoveryCode_DoesNotConsumeTheCode()
    {
        var (user, _) = TotpUser("mypassword");
        user.TotpRecoveryCodes = JsonSerializer.Serialize(new List<string> { BCrypt.Net.BCrypt.HashPassword("ABCDE12345") });
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.DisableTotpAsync(1, Disable("wrong", "ABCDE-12345", recovery: true), ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        await _repoMock.DidNotReceive().ReplaceTotpRecoveryCodesAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static TotpDisableRequest Disable(string password, string code, bool recovery = false)
        => new() { Password = password, Code = code, UseRecoveryCode = recovery };

    // An account with 2FA on, its password, and the authenticator code valid at FixedNow.
    private static (User User, string Code) TotpUser(string password)
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        var user = new User
        {
            Id = 1,
            Username = "admin",
            TotpEnabled = true,
            TotpSecret = $"enc:{Base32Encoding.ToString(secret)}",
            TotpRecoveryCodes = "[\"hash1\"]",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            SecurityStamp = "stamp-before"
        };
        return (user, new Totp(secret, step: 30, totpSize: 6).ComputeTotp(FixedNow.UtcDateTime));
    }

    // --- ConsumeRecoveryCodeAsync ---

    [Fact]
    public async Task ConsumeRecoveryCodeAsync_UserNotFound_ReturnsFalse()
    {
        _repoMock.FindUserByIdTrackedAsync(99, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _sut.ConsumeRecoveryCodeAsync(99, "ABCDE-12345", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ConsumeRecoveryCodeAsync_ValidCode_RemovesAndReturnsTrue()
    {
        var plainCode = "ABCDE12345";
        var hashedCode = BCrypt.Net.BCrypt.HashPassword(plainCode);
        var otherHash = BCrypt.Net.BCrypt.HashPassword("OTHER67890");
        var hashes = JsonSerializer.Serialize(new List<string> { hashedCode, otherHash });

        var user = new User
        {
            Id = 1,
            Username = "admin",
            TotpRecoveryCodes = hashes
        };
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);
        string? written = null;
        _repoMock.ReplaceTotpRecoveryCodesAsync(1, hashes, Arg.Do<string>(value => written = value), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.ConsumeRecoveryCodeAsync(1, "ABCDE-12345", TestContext.Current.CancellationToken);

        Assert.True(result);
        // One code was removed, conditionally on the list that was read.
        Assert.Equal([otherHash], JsonSerializer.Deserialize<List<string>>(written!)!);
        await _auditMock.Received(1).LogAsync("RecoveryCodeUsed", "User", 1, "admin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeRecoveryCodeAsync_ConcurrentConsumeOfTheSameCodeWon_ReturnsFalse()
    {
        var hashedCode = BCrypt.Net.BCrypt.HashPassword("ABCDE12345");
        var otherHash = BCrypt.Net.BCrypt.HashPassword("OTHER67890");
        var read = JsonSerializer.Serialize(new List<string> { hashedCode, otherHash });
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "admin", TotpRecoveryCodes = read });
        // The other request rewrote the list first: the conditional replace matches nothing, and the
        // stored list no longer holds the code.
        _repoMock.ReplaceTotpRecoveryCodesAsync(1, read, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.GetTotpRecoveryCodesAsync(1, Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(new List<string> { otherHash }));

        var result = await _sut.ConsumeRecoveryCodeAsync(1, "ABCDE-12345", TestContext.Current.CancellationToken);

        Assert.False(result);
        await _auditMock.DidNotReceive().LogAsync("RecoveryCodeUsed", Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeRecoveryCodeAsync_ConcurrentConsumeOfAnotherCode_RetriesOnTheFreshList()
    {
        var hashedCode = BCrypt.Net.BCrypt.HashPassword("ABCDE12345");
        var otherHash = BCrypt.Net.BCrypt.HashPassword("OTHER67890");
        var read = JsonSerializer.Serialize(new List<string> { hashedCode, otherHash });
        var fresh = JsonSerializer.Serialize(new List<string> { hashedCode });
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 1, Username = "admin", TotpRecoveryCodes = read });
        _repoMock.ReplaceTotpRecoveryCodesAsync(1, read, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.GetTotpRecoveryCodesAsync(1, Arg.Any<CancellationToken>()).Returns(fresh);
        _repoMock.ReplaceTotpRecoveryCodesAsync(1, fresh, "[]", Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.ConsumeRecoveryCodeAsync(1, "ABCDE-12345", TestContext.Current.CancellationToken);

        // The concurrent removal of the other code is kept: the write starts from the fresh list.
        Assert.True(result);
        await _repoMock.Received(1).ReplaceTotpRecoveryCodesAsync(1, fresh, "[]", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeRecoveryCodeAsync_InvalidCode_ReturnsFalse()
    {
        var hashedCode = BCrypt.Net.BCrypt.HashPassword("VALIDCODE1");
        var hashes = JsonSerializer.Serialize(new List<string> { hashedCode });

        var user = new User
        {
            Id = 1,
            Username = "admin",
            TotpRecoveryCodes = hashes
        };
        _repoMock.FindUserByIdTrackedAsync(1, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.ConsumeRecoveryCodeAsync(1, "WRONG-CODE0", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    // --- ValidateTotpCode ---

    [Fact]
    public void ValidateTotpCode_ValidCode_ReturnsTrue()
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(secret);
        var totp = new Totp(secret, step: 30, totpSize: 6);
        var code = totp.ComputeTotp(FixedNow.UtcDateTime);

        var result = _sut.ValidateTotpCode($"enc:{base32Secret}", code);

        Assert.True(result);
    }

    [Fact]
    public void ValidateTotpCode_CodeOutsideAcceptedWindow_ReturnsFalse()
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(secret);
        var totp = new Totp(secret, step: 30, totpSize: 6);
        var expiredCode = totp.ComputeTotp(FixedNow.AddMinutes(-2).UtcDateTime);

        var result = _sut.ValidateTotpCode($"enc:{base32Secret}", expiredCode);

        Assert.False(result);
    }

    [Fact]
    public void ValidateTotpCode_InvalidCode_ReturnsFalse()
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(secret);

        var result = _sut.ValidateTotpCode($"enc:{base32Secret}", "000000");

        Assert.False(result);
    }
}
