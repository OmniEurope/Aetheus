// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The certificate inventory is read straight from the Let's Encrypt live directory, one
/// sub-directory per lineage: its name, the domains and the expiry of the leaf, and the two paths. A lineage
/// without a readable chain is left out rather than failing the inventory. The live directory is a
/// temporary one, so none of this needs certbot or a Linux host.</summary>
public sealed class CertbotCollectorLiveDirectoryTests : IDisposable
{
    private readonly DirectoryInfo _live = Directory.CreateTempSubdirectory("aetheus-letsencrypt-");

    public void Dispose() => _live.Delete(recursive: true);

    private static CertbotCollector Build(bool unixLike = true) =>
        new(
            NullLogger<CertbotCollector>.Instance,
            Substitute.For<IShellRunner>(),
            collectCertificates: null,
            fileExists: _ => false,
            unixLike: unixLike,
            readHelper: _ => null);

    private string AddLineage(string name, string? subject, DateTimeOffset notAfter, params string[] dnsNames)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_live.FullName, name)).FullName;
        if (subject is null)
            return directory;

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames) names.AddDnsName(dns);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter);
        File.WriteAllText(Path.Combine(directory, "fullchain.pem"), certificate.ExportCertificatePem());
        return directory;
    }

    [Fact]
    public void EachLineageWithAChain_IsListedWithItsNameDomainsExpiryAndPaths()
    {
        var notAfter = DateTimeOffset.UtcNow.AddDays(60);
        var directory = AddLineage("example.com", "CN=example.com", notAfter, "example.com", "www.example.com");

        var certificate = Assert.Single(Build().CollectCertificatesFrom(_live.FullName));

        Assert.Equal("example.com", certificate.Name);
        Assert.Equal(["example.com", "www.example.com"], certificate.Domains);
        Assert.Equal(notAfter.UtcDateTime, certificate.ExpiryDate, TimeSpan.FromSeconds(1));
        Assert.Equal(Path.Combine(directory, "fullchain.pem"), certificate.CertPath);
        Assert.Equal(Path.Combine(directory, "privkey.pem"), certificate.KeyPath);
    }

    [Fact]
    public void SeveralLineages_AreAllListed()
    {
        AddLineage("a.example", "CN=a.example", DateTimeOffset.UtcNow.AddDays(10), "a.example");
        AddLineage("b.example", "CN=b.example", DateTimeOffset.UtcNow.AddDays(20), "b.example");

        var names = Build().CollectCertificatesFrom(_live.FullName).Select(c => c.Name).Order().ToArray();

        Assert.Equal(["a.example", "b.example"], names);
    }

    [Fact]
    public void ALineageWithoutAChain_IsSkipped()
    {
        AddLineage("empty.example", subject: null, DateTimeOffset.UtcNow);
        AddLineage("ok.example", "CN=ok.example", DateTimeOffset.UtcNow.AddDays(10), "ok.example");

        var certificate = Assert.Single(Build().CollectCertificatesFrom(_live.FullName));

        Assert.Equal("ok.example", certificate.Name);
    }

    [Fact]
    public void ALineageWhoseChainIsNotACertificate_IsSkippedAndTheOthersStillListed()
    {
        var broken = AddLineage("broken.example", subject: null, DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(broken, "fullchain.pem"), "this is not a certificate");
        AddLineage("ok.example", "CN=ok.example", DateTimeOffset.UtcNow.AddDays(10), "ok.example");

        var certificate = Assert.Single(Build().CollectCertificatesFrom(_live.FullName));

        Assert.Equal("ok.example", certificate.Name);
    }

    [Fact]
    public void AMissingLiveDirectory_GivesAnEmptyInventory()
    {
        var missing = Path.Combine(_live.FullName, "does-not-exist");

        Assert.Empty(Build().CollectCertificatesFrom(missing));
    }

    [Fact]
    public void OnAPlatformWithoutALiveDirectory_GivesAnEmptyInventoryEvenWhenOneExists()
    {
        AddLineage("example.com", "CN=example.com", DateTimeOffset.UtcNow.AddDays(10), "example.com");

        Assert.Empty(Build(unixLike: false).CollectCertificatesFrom(_live.FullName));
    }
}
