// SPDX-License-Identifier: EUPL-1.2
// Test corpus for .aetheus/security-rules/opengrep/aetheus-csharp-crypto.yml. Not compiled.
public class CryptoRuleCorpus
{
    public void Hashes(byte[] data)
    {
        // ruleid: aetheus.csharp.weak-hash-md5
        using var md5 = MD5.Create();
        // ruleid: aetheus.csharp.weak-hash-md5
        var digest = MD5.HashData(data);
        // ruleid: aetheus.csharp.weak-hash-md5
        var legacy = new MD5CryptoServiceProvider();
        // ok: aetheus.csharp.weak-hash-md5
        using var sha256 = SHA256.Create();
        // ok: aetheus.csharp.weak-hash-md5
        var strong = SHA256.HashData(data);

        // ruleid: aetheus.csharp.weak-hash-sha1
        using var sha1 = SHA1.Create();
        // ruleid: aetheus.csharp.weak-hash-sha1
        var other = SHA1.HashData(data);
        // ruleid: aetheus.csharp.weak-hash-sha1
        var managed = new SHA1Managed();
        // ok: aetheus.csharp.weak-hash-sha1
        var stronger = SHA512.HashData(data);
    }

    public void Ciphers()
    {
        // ruleid: aetheus.csharp.weak-cipher
        var des = DES.Create();
        // ruleid: aetheus.csharp.weak-cipher
        var triple = new TripleDESCryptoServiceProvider();
        // ruleid: aetheus.csharp.weak-cipher
        var rc2 = RC2.Create();
        // ok: aetheus.csharp.weak-cipher
        var aes = Aes.Create();
    }

    public void Modes(Aes aes, byte[] data)
    {
        // ruleid: aetheus.csharp.ecb-mode
        aes.Mode = CipherMode.ECB;
        // ruleid: aetheus.csharp.ecb-mode
        var encrypted = aes.EncryptEcb(data, PaddingMode.PKCS7);
        // ok: aetheus.csharp.ecb-mode
        aes.Mode = CipherMode.CBC;
    }

    public string CreateSessionToken()
    {
        // ruleid: aetheus.csharp.random-for-secret
        var random = new Random();
        return random.Next().ToString();
    }

    public string NewApiKey()
    {
        // ruleid: aetheus.csharp.random-for-secret
        return Random.Shared.Next().ToString();
    }

    public string CreatePasswordResetToken()
    {
        // ok: aetheus.csharp.random-for-secret
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public TimeSpan RetryDelay()
    {
        // ok: aetheus.csharp.random-for-secret
        return TimeSpan.FromMilliseconds(Random.Shared.Next(100, 300));
    }

    public void Keys()
    {
        // ruleid: aetheus.csharp.rsa-short-key
        var small = new RSACryptoServiceProvider(1024);
        // ruleid: aetheus.csharp.rsa-short-key
        var created = RSA.Create(512);
        // ok: aetheus.csharp.rsa-short-key
        var good = RSA.Create(3072);
        // ok: aetheus.csharp.rsa-short-key
        var standard = new RSACryptoServiceProvider(2048);
    }

    public void StaticKeys(Aes aes, string loadedKey)
    {
        // ruleid: aetheus.csharp.hard-coded-crypto-key
        aes.Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
        // ruleid: aetheus.csharp.hard-coded-crypto-key
        aes.IV = new byte[16];
        // ruleid: aetheus.csharp.hard-coded-crypto-key
        var signing = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("super-secret-signing-key-value"));
        // ok: aetheus.csharp.hard-coded-crypto-key
        aes.Key = Convert.FromBase64String(loadedKey);
        // ok: aetheus.csharp.hard-coded-crypto-key
        aes.GenerateIV();
        // ok: aetheus.csharp.hard-coded-crypto-key
        var configured = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(loadedKey));
    }

    public void Derivation(string password, byte[] salt)
    {
        // ruleid: aetheus.csharp.weak-key-derivation
        var sha1Default = new Rfc2898DeriveBytes(password, salt, 100000);
        // ruleid: aetheus.csharp.weak-key-derivation
        var cheap = new Rfc2898DeriveBytes(password, salt, 1000, HashAlgorithmName.SHA256);
        // ruleid: aetheus.csharp.weak-key-derivation
        var staticCheap = Rfc2898DeriveBytes.Pbkdf2(password, salt, 10000, HashAlgorithmName.SHA256, 32);
        // ok: aetheus.csharp.weak-key-derivation
        var strong = new Rfc2898DeriveBytes(password, salt, 600000, HashAlgorithmName.SHA256);
        // ok: aetheus.csharp.weak-key-derivation
        var modern = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA512, 64);
    }

    public void Protocols(SslClientAuthenticationOptions options, HttpClientHandler handler)
    {
        // ruleid: aetheus.csharp.deprecated-tls-protocol
        options.EnabledSslProtocols = SslProtocols.Tls11;
        // ruleid: aetheus.csharp.deprecated-tls-protocol
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3;
        // ok: aetheus.csharp.deprecated-tls-protocol
        options.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        // ok: aetheus.csharp.deprecated-tls-protocol
        handler.SslProtocols = SslProtocols.None;
    }

    public void Certificates(Stream inner)
    {
        // ruleid: aetheus.csharp.certificate-validation-always-true
        ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, errors) => true;
        // ruleid: aetheus.csharp.certificate-validation-always-true
        ServicePointManager.ServerCertificateValidationCallback += delegate { return true; };
        // ruleid: aetheus.csharp.certificate-validation-always-true
        var stream = new SslStream(inner, false, (sender, certificate, chain, errors) => true);
        // ruleid: aetheus.csharp.certificate-validation-always-true
        var handler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } };
        // ok: aetheus.csharp.certificate-validation-always-true
        var checkedStream = new SslStream(inner, false, (sender, certificate, chain, errors) => errors == SslPolicyErrors.None);
        // ok: aetheus.csharp.certificate-validation-always-true
        var strict = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = ValidateAgainstPin } };
    }
}
