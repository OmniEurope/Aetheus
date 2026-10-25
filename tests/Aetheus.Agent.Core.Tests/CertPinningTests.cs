// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;

namespace Aetheus.Agent.Core.Tests;

// S-FEAT-24: certificate pinning accepts only the matching server cert.
public class CertPinningTests
{
    private static X509Certificate2 MakeSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=aetheus-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void Pinning_AcceptsMatchingThumbprint_RejectsOthers()
    {
        using var cert = MakeSelfSigned();
        using var other = MakeSelfSigned();
        var thumb = cert.GetCertHashString(HashAlgorithmName.SHA256);

        var cb = AgentTlsPosture.BuildCertValidationCallback(
            new AetheusAgentOptions { PinnedServerCertThumbprint = thumb });

        Assert.NotNull(cb);
        Assert.True(cb!(this, cert, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(cb(this, other, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void NoPinNoInsecure_UsesDefaultValidation()
    {
        Assert.Null(AgentTlsPosture.BuildCertValidationCallback(new AetheusAgentOptions()));
    }

    [Fact]
    public void AllowInsecure_AcceptsAny_WhenNoPin()
    {
        using var cert = MakeSelfSigned();
        var cb = AgentTlsPosture.BuildCertValidationCallback(
            new AetheusAgentOptions { AllowInsecureCerts = true });
        Assert.NotNull(cb);
        Assert.True(cb!(this, cert, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
