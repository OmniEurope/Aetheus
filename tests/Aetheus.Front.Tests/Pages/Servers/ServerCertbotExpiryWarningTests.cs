// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-69: the Certbot page marks a certificate "expires soon" from the longest configured alert lead time,
/// not from a fixed 30 days.</summary>
public sealed class ServerCertbotExpiryWarningTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerCertbotExpiryWarningTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static ServerDetailDto MakeServer() => new()
    {
        Id = 10,
        Name = "cert-srv",
        Hostname = "10.0.0.10",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Certbot = new CertbotDataDto
        {
            IsInstalled = true,
            Version = "2.11.0",
            Certificates =
            [
                new CertbotCertificateDto { Name = "soon.example.com", Domains = ["soon.example.com"], ExpiryDate = DateTime.UtcNow.AddDays(5) },
                new CertbotCertificateDto { Name = "twenty.example.com", Domains = ["twenty.example.com"], ExpiryDate = DateTime.UtcNow.AddDays(20) }
            ]
        }
    };

    private IRenderedComponent<ServerCertbotSection> RenderSection()
    {
        var server = MakeServer();
        return Render<ServerCertbotSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));
    }

    private static int Badges(IRenderedComponent<ServerCertbotSection> cut) =>
        cut.FindAll(".omni-badge").Count(badge => badge.TextContent.Contains("ExpiringSoon", StringComparison.Ordinal));

    [Fact]
    public void WithALongestLeadTimeOfSevenDays_OnlyTheCertificateWithinSevenDaysIsMarked()
    {
        _handler.SetJsonResponse("api/certbot/expiry-warning", new CertificateExpiryWarningDto { Days = 7 });

        var cut = RenderSection();

        cut.WaitForAssertion(() => Assert.Equal(1, Badges(cut)));
    }

    [Fact]
    public void WithALongestLeadTimeOfSixtyDays_BothCertificatesAreMarked()
    {
        _handler.SetJsonResponse("api/certbot/expiry-warning", new CertificateExpiryWarningDto { Days = 60 });

        var cut = RenderSection();

        cut.WaitForAssertion(() => Assert.Equal(2, Badges(cut)));
    }

    [Fact]
    public void WhenThePlatformCannotAnswer_TheFixedThirtyDaysStillApply()
    {
        _handler.SetResponse("api/certbot/expiry-warning", System.Net.HttpStatusCode.InternalServerError);

        var cut = RenderSection();

        // Both certificates expire within 30 days of the fixed fallback horizon (5 and 20 days).
        cut.WaitForAssertion(() => Assert.Equal(2, Badges(cut)));
    }
}
