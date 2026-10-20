// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The domains shown for a Let's Encrypt certificate come from the leaf itself: its subject alternative
/// names first, and the common name only when the certificate carries no alternative name.</summary>
public class CertbotDomainExtractionTests
{
    private static X509Certificate2 Certificate(string subject, params string[] dnsNames)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (dnsNames.Length > 0)
        {
            var names = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames) names.AddDnsName(name);
            request.CertificateExtensions.Add(names.Build());
        }
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public void ExtractDomains_ACertificateWithAlternativeNames_ListsEachOfThem()
    {
        using var certificate = Certificate("CN=ignored.example", "example.com", "www.example.com");

        var domains = CertbotCollector.ExtractDomains(certificate);

        Assert.Equal(["example.com", "www.example.com"], domains);
    }

    [Fact]
    public void ExtractDomains_ACertificateWithoutAlternativeNames_FallsBackToTheCommonName()
    {
        using var certificate = Certificate("CN=host.example.org");

        var domains = CertbotCollector.ExtractDomains(certificate);

        Assert.Equal(["host.example.org"], domains);
    }
}
