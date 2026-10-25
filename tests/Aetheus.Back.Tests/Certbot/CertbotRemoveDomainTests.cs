// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Certbot;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Recette R-12: a certificate is reissued without one of its names through a typed operation; only a
/// name the server reported for that certificate can go, and never its last one.</summary>
public class CertbotRemoveDomainTests
{
    private readonly ICertbotRepository _repo = Substitute.For<ICertbotRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _taskService = Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly CertbotService _sut;
    private ServerTask? _queued;

    public CertbotRemoveDomainTests()
    {
        _sut = new CertbotService(_repo, _audit, _taskService);
        _repo.AddTaskAsync(Arg.Do<ServerTask>(task => _queued = task), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repo.GetCertificatesAsync(3, Arg.Any<CancellationToken>()).Returns(
        [
            Certificate("aetheus.sonytumen.com", "aetheus.sonytumen.com", "app.aetheus.sonytumen.com", "aetheus-api.sonytumen.com"),
            Certificate("solo.example.com", "solo.example.com")
        ]);
    }

    private static CertbotCertificate Certificate(string name, params string[] domains) => new()
    {
        Name = name,
        Domains = System.Text.Json.JsonSerializer.Serialize(domains),
        ExpiryDate = new DateTime(2027, 1, 1),
        CertPath = $"/etc/letsencrypt/live/{name}/fullchain.pem",
        KeyPath = $"/etc/letsencrypt/live/{name}/privkey.pem"
    };

    private Task Remove(string? certificate, string? domain) => _sut.ExecuteActionAsync(3,
        new CertbotActionRequest { Action = CertbotAction.RemoveDomain, CertificateName = certificate, DomainName = domain },
        TestContext.Current.CancellationToken);

    [Fact]
    public async Task RemoveDomain_QueuesTheTypedOperation_TargetIsTheCertificate_TheNameTravelsInTheTaskEnvironment()
    {
        await Remove("aetheus.sonytumen.com", "aetheus-api.sonytumen.com");

        Assert.NotNull(_queued);
        Assert.Equal(ExecutorType.Operation, _queued.Executor);
        Assert.Equal(OperationKind.CertbotRemoveDomain, _queued.Operation);
        Assert.Equal("aetheus.sonytumen.com", _queued.Command);
        var env = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(_queued.EnvironmentVariables!)!;
        Assert.Equal("aetheus-api.sonytumen.com", env[CertbotTaskVariables.RemoveDomain]);
        await _audit.Received(1).LogAsync("CertbotRemoveDomain", "Certbot", 3,
            "aetheus.sonytumen.com: aetheus-api.sonytumen.com", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("aetheus.sonytumen.com", "other.example.com")]   // not covered by the certificate
    [InlineData("solo.example.com", "solo.example.com")]         // its last name
    [InlineData("missing.example.com", "missing.example.com")]   // unknown certificate
    [InlineData("aetheus.sonytumen.com", "a.example.com,b.example.com")]
    [InlineData("aetheus.sonytumen.com", "aetheus-api.sonytumen.com\n")]
    [InlineData("aetheus.sonytumen.com", "-d")]
    [InlineData("aetheus.sonytumen.com", null)]
    [InlineData("../live", "aetheus-api.sonytumen.com")]
    [InlineData(null, "aetheus-api.sonytumen.com")]
    public async Task RemoveDomain_RefusesBeforeQueueingAnything(string? certificate, string? domain)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Remove(certificate, domain));
        Assert.Null(_queued);
    }
}
