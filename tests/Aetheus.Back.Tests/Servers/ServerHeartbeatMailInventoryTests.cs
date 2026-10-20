// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>The heartbeat persists the full mail state and hands the inventory to the Mail module
/// through a domain event (Servers never calls Mail directly).</summary>
public sealed class ServerHeartbeatMailInventoryTests
{
    private readonly IServerRepository _repo = Substitute.For<IServerRepository>();
    private readonly IServerHeartbeatRepository _heartbeatRepo = Substitute.For<IServerHeartbeatRepository>();
    private readonly IDomainEventDispatcher _events = Substitute.For<IDomainEventDispatcher>();
    private readonly ServerService _sut;

    public ServerHeartbeatMailInventoryTests()
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(Substitute.For<IHubClients>());
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(new Server { Id = 1, Name = "mx", Hostname = "mx" });
        _sut = new ServerService(_repo, _heartbeatRepo, hub, alertHub, Substitute.For<IAuditService>(),
            Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), TimeProvider.System, Substitute.For<IDbTransactionScope>(),
            domainEvents: _events);
    }

    [Fact]
    public async Task InstalledMailStack_IsPersistedAndPublishedToTheMailModule()
    {
        var heartbeat = new ServerHeartbeatDto
        {
            Mail = new MailDataDto
            {
                IsInstalled = true,
                Hostname = "mail.example.com",
                HelperVersion = 2,
                AccountsCollected = true,
                Accounts = [new MailAccountDto { Email = "bob@example.com", Domain = "example.com" }],
                Tls = new MailTlsStateDto { CertPath = "/etc/ssl/certs/mail.pem", IsReadable = true, IsSelfSigned = true },
                SpamFilter = new MailSpamFilterStateDto { Name = "rspamd", IsInstalled = true, IsRunning = true, RejectScore = 15 },
                Diagnostics = ["aliases-skipped|2"]
            }
        };

        await _sut.ProcessHeartbeatAsync(1, heartbeat, ct: TestContext.Current.CancellationToken);

        await _heartbeatRepo.Received(1).ReplaceMailDataAsync(1, Arg.Is<MailState?>(s =>
            s != null && s.Hostname == "mail.example.com" && s.HelperVersion == 2 && s.TlsIsSelfSigned
            && s.SpamFilterName == "rspamd" && s.SpamRejectScore == 15 && s.DiagnosticsJson!.Contains("aliases-skipped")),
            Arg.Any<CancellationToken>());
        _events.Received(1).Publish(Arg.Is<MailInventoryReportedEvent>(e =>
            e.ServerId == 1 && e.Mail.Accounts.Count == 1 && e.Mail.AccountsCollected));
    }

    [Fact]
    public async Task AbsentMailStack_ClearsTheStateAndPublishesNothing()
    {
        await _sut.ProcessHeartbeatAsync(1, new ServerHeartbeatDto(), ct: TestContext.Current.CancellationToken);

        await _heartbeatRepo.Received(1).ReplaceMailDataAsync(1, null, Arg.Any<CancellationToken>());
        _events.DidNotReceive().Publish(Arg.Any<MailInventoryReportedEvent>());
    }
}
