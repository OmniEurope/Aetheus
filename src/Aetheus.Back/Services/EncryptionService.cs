// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Back.Services;

/// <summary>
/// AES-256-GCM authenticated encryption with a 1-byte version prefix to allow future key rotation.
/// Layout (v2): [version=0x02][nonce(12)][tag(16)][ciphertext(N)] - key derived via HKDF-SHA256(passphrase, salt=Auth:EncryptionSalt, info="aetheus-aes256-v2").
/// Layout (v1): [version=0x01][nonce(12)][tag(16)][ciphertext(N)] - key derived via raw SHA-256(passphrase). Read-only legacy.
/// Legacy AES-CBC ciphertext (no version byte, 16-byte IV prefix, no auth tag) is decrypted on a best-effort
/// basis so the service can read records written before the migration.
/// </summary>
public sealed class EncryptionService(IConfiguration config, IHostEnvironment environment) : IEncryptionService
{
    private const byte VersionGcmV1 = 0x01;
    private const byte VersionGcmV2 = 0x02;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("aetheus-aes256-v2");

    // Perf (#27): EncryptionService is registered as a singleton. The HKDF derivation runs once
    // per process; subsequent encrypt/decrypt calls reuse the cached key bytes.
    private byte[]? _keyV2Cache;
    private byte[]? _keyV1LegacyCache;
    private readonly object _keyLock = new();

    public string EncryptValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var key = DeriveKeyV2();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var result = new byte[1 + NonceSize + TagSize + cipher.Length];
        result[0] = VersionGcmV2;
        Buffer.BlockCopy(nonce, 0, result, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, result, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, result, 1 + NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(result);
    }

    public string DecryptValue(string encryptedBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(encryptedBase64);

        var raw = Convert.FromBase64String(encryptedBase64);

        if (raw.Length > 0 && (raw[0] == VersionGcmV1 || raw[0] == VersionGcmV2))
        {
            if (raw.Length < 1 + NonceSize + TagSize)
                throw new CryptographicException("Encrypted payload is too short.");

            var key = raw[0] == VersionGcmV2 ? DeriveKeyV2() : DeriveKeyV1Legacy();
            var nonce = raw.AsSpan(1, NonceSize);
            var tag = raw.AsSpan(1 + NonceSize, TagSize);
            var cipher = raw.AsSpan(1 + NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }

        // Legacy AES-CBC fallback. Hardening (#21): off by default - enable Auth:AllowLegacyCbc
        // only during a one-time migration window. Once all records have been re-encrypted via a
        // round-trip Decrypt+Encrypt, this branch must be deleted.
        var allowLegacy = config.GetValue("Auth:AllowLegacyCbc", false);
        if (!allowLegacy)
            throw new CryptographicException("Legacy AES-CBC ciphertext is rejected. Enable Auth:AllowLegacyCbc only while running the re-encryption tool.");

        return DecryptLegacyCbc(raw, DeriveKeyV1Legacy());
    }

    private static string DecryptLegacyCbc(byte[] raw, byte[] keyBytes)
    {
        if (raw.Length <= 16)
            throw new CryptographicException("Legacy ciphertext is malformed.");

        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = raw[..16];
        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(raw, 16, raw.Length - 16);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] DeriveKeyV2()
    {
        if (_keyV2Cache is not null) return _keyV2Cache;
        lock (_keyLock)
        {
            if (_keyV2Cache is not null) return _keyV2Cache;
            var passphrase = GetConfiguredPassphrase();
            var salt = GetConfiguredSalt();
            var ikm = Encoding.UTF8.GetBytes(passphrase);
            _keyV2Cache = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, outputLength: 32, salt: salt, info: HkdfInfo);
            return _keyV2Cache;
        }
    }

    // Legacy v1 derivation kept for read-only decryption of pre-HKDF records.
    private byte[] DeriveKeyV1Legacy()
    {
        if (_keyV1LegacyCache is not null) return _keyV1LegacyCache;
        lock (_keyLock)
        {
            if (_keyV1LegacyCache is not null) return _keyV1LegacyCache;
            var passphrase = GetConfiguredPassphrase();
            // nosemgrep: aetheus.csharp.password-fast-hash - legacy v1 key kept only to decrypt pre-HKDF records.
            _keyV1LegacyCache = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
            return _keyV1LegacyCache;
        }
    }

    private string GetConfiguredPassphrase()
    {
        var key = config["Auth:EncryptionKey"];
        if (string.IsNullOrEmpty(key))
        {
            // Fail-fast in any non-Development environment, and even in Development
            // require an explicit key in appsettings.Development.json - no hardcoded fallback.
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    "Auth:EncryptionKey must be configured in non-development environments.");

            throw new InvalidOperationException(
                "Auth:EncryptionKey must be configured (set it in appsettings.Development.json).");
        }
        return key;
    }

    private byte[] GetConfiguredSalt()
    {
        var saltConfigured = config["Auth:EncryptionSalt"];
        if (!string.IsNullOrEmpty(saltConfigured))
        {
            var bytes = Convert.FromBase64String(saltConfigured);
            if (bytes.Length < 16)
                throw new InvalidOperationException("Auth:EncryptionSalt must decode to at least 16 bytes.");
            return bytes;
        }

        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "Auth:EncryptionSalt must be configured (base64, >= 16 bytes) in non-development environments.");

        // Deterministic dev-only salt derived from the passphrase so re-runs decrypt prior dev data.
        // nosemgrep: aetheus.csharp.password-fast-hash - dev-only salt, not a stored password hash.
        return SHA256.HashData(Encoding.UTF8.GetBytes("aetheus-dev-salt:" + GetConfiguredPassphrase()));
    }
}
