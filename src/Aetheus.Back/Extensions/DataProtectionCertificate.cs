// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aetheus.Back.Extensions;

/// <summary>
/// The certificate that encrypts the Data Protection key ring at rest. It lives in the key directory beside the keys it
/// protects (the container entrypoint creates it the same way on first start). A host that starts without that
/// entrypoint (a local run, a binary install) creates it here, so the key ring is encrypted wherever it is persisted and
/// the framework never writes a key "in unencrypted form" (R-1: the <c>XmlKeyManager</c> warning 35 of every start).
/// </summary>
internal static class DataProtectionCertificate
{
    internal const string FileName = "dp-cert.pfx";

    private const string Subject = "CN=Aetheus Data Protection";
    private const int ValidityDays = 36500;

    /// <summary>Loads the certificate of <paramref name="keyDirectory"/>, creating it first when it does not exist yet.
    /// Two hosts starting together (a blue-green overlap, parallel test hosts) end with the same file: the certificate is
    /// written under a temporary name and moved into place, and the host that loses the race loads the winner's.</summary>
    public static X509Certificate2 LoadOrCreate(string keyDirectory)
    {
        var path = Path.Combine(keyDirectory, FileName);
        if (!File.Exists(path)) TryCreate(path);
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
    }

    private static void TryCreate(string path)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, Generate());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another host created it between the check and the move: its certificate is the one to use.
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] Generate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = TimeProvider.System.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(ValidityDays));
        return certificate.Export(X509ContentType.Pfx);
    }
}
