// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text.Json;
using OtpNet;

namespace Aetheus.Back.Components.Auth;

public class TotpService(
    IAuthRepository repo,
    IEncryptionService encryption,
    IAuditService audit,
    JwtOptions jwtOptions,
    TimeProvider timeProvider) : ITotpService
{
    public async Task<TotpSetupResponse> SetupTotpAsync(int userId, CancellationToken ct = default)
    {
        var user = await repo.FindUserByIdTrackedAsync(userId, ct).ConfigureAwait(false)
                   ?? throw new NotFoundException("User not found.");
        if (user.TotpEnabled)
            throw new ConflictException("TOTP is already enabled. Disable it first.");

        var secret = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(secret);

        var recoveryCodes = Enumerable.Range(0, 8)
            .Select(_ => GenerateRecoveryCode())
            .ToList();
        var hashedCodes = recoveryCodes
            .Select(c => BCrypt.Net.BCrypt.HashPassword(c.Replace("-", "", StringComparison.Ordinal)))
            .ToList();

        user.TotpSecret = encryption.EncryptValue(base32Secret);
        user.TotpRecoveryCodes = JsonSerializer.Serialize(hashedCodes);
        user.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        var issuer = Uri.EscapeDataString(jwtOptions.Issuer);
        var account = Uri.EscapeDataString(user.Username);
        var uri = $"otpauth://totp/{issuer}:{account}?secret={base32Secret}&issuer={issuer}&digits=6&period=30";

        return new TotpSetupResponse
        {
            SharedKey = base32Secret,
            AuthenticatorUri = uri,
            RecoveryCodes = recoveryCodes
        };
    }

    public async Task<bool> VerifyAndEnableTotpAsync(int userId, string code, CancellationToken ct = default)
    {
        var user = await repo.FindUserByIdTrackedAsync(userId, ct).ConfigureAwait(false);
        if (user is null || string.IsNullOrEmpty(user.TotpSecret)) return false;
        if (user.TotpEnabled) return true;

        if (!ValidateTotpCode(user.TotpSecret, code)) return false;

        user.TotpEnabled = true;
        user.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("TotpEnabled", "User", userId, user.Username, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DisableTotpAsync(int userId, string password, CancellationToken ct = default)
    {
        var user = await repo.FindUserByIdTrackedAsync(userId, ct).ConfigureAwait(false);
        if (user is null || !user.TotpEnabled) return false;

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash)) return false;

        user.TotpSecret = null;
        user.TotpEnabled = false;
        user.TotpRecoveryCodes = null;
        user.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("TotpDisabled", "User", userId, user.Username, ct).ConfigureAwait(false);
        return true;
    }

    public bool ValidateTotpCode(string encryptedSecret, string code)
    {
        var base32Secret = encryption.DecryptValue(encryptedSecret);
        var secretBytes = Base32Encoding.ToBytes(base32Secret);
        var totp = new Totp(secretBytes, step: 30, totpSize: 6);
        return totp.VerifyTotp(
            timeProvider.GetUtcNow().UtcDateTime,
            code,
            out _,
            new VerificationWindow(previous: 1, future: 1));
    }

    public async Task<bool> ConsumeRecoveryCodeAsync(int userId, string code, CancellationToken ct)
    {
        var user = await repo.FindUserByIdTrackedAsync(userId, ct).ConfigureAwait(false);
        if (user is null || string.IsNullOrEmpty(user.TotpRecoveryCodes)) return false;

        var normalized = code.Replace("-", "", StringComparison.Ordinal);
        var current = user.TotpRecoveryCodes;
        // BACK-011: the list is rewritten only if nobody changed it since it was read. A lost race
        // re-reads the stored list: the same code is then gone (false, no second login), while a
        // different code consumed concurrently is still there and is retried on the fresh list.
        for (var attempt = 0; attempt < MaxRecoveryConsumeAttempts && !string.IsNullOrEmpty(current); attempt++)
        {
            var hashes = JsonSerializer.Deserialize<List<string>>(current) ?? [];
            var index = hashes.FindIndex(hash => BCrypt.Net.BCrypt.Verify(normalized, hash));
            if (index < 0) return false;

            hashes.RemoveAt(index);
            if (await repo.ReplaceTotpRecoveryCodesAsync(userId, current, JsonSerializer.Serialize(hashes), ct).ConfigureAwait(false))
            {
                await audit.LogAsync("RecoveryCodeUsed", "User", userId, user.Username, ct).ConfigureAwait(false);
                return true;
            }
            current = await repo.GetTotpRecoveryCodesAsync(userId, ct).ConfigureAwait(false);
        }
        return false;
    }

    private const int MaxRecoveryConsumeAttempts = 3;

    private static string GenerateRecoveryCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(5);
        var hex = Convert.ToHexString(bytes).ToUpperInvariant();
        return $"{hex[..5]}-{hex[5..]}";
    }
}
