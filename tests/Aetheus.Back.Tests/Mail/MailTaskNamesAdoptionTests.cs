// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Mail;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R-12: the mail page finds again, when the user comes back, the tasks of its domain, account, alias and DKIM
/// actions by the names in <see cref="ServerTaskNames"/>, and must not take the tasks other components follow (the
/// setup, the leftovers check, the purge). The names the backend gives come from the same constants.
/// </summary>
public sealed class MailTaskNamesAdoptionTests
{
    private readonly IMailRepository _repo = Substitute.For<IMailRepository>();
    private readonly List<string> _names = [];
    private readonly MailService _sut;

    public MailTaskNamesAdoptionTests()
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var servers = Substitute.For<IServerRepository>();
        servers.FindServerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Name = "srv", Hostname = "h", MailSetupAvailable = true });
        _repo.GetStateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new MailState { HelperVersion = MailService.RetryTuningMinimumHelperVersion });
        _repo.AddTaskAsync(Arg.Do<ServerTask>(task => _names.Add(task.Name)), Arg.Any<CancellationToken>());
        _sut = new MailService(_repo, Substitute.For<IAuditService>(), encryption, servers,
            Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(), Substitute.For<IMailMasterSecrets>());
    }

    private static bool IsAdopted(string name) =>
        ServerTaskNames.MailAdoptedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task TheDomainAccountAndServiceActions_AreNamedByAnAdoptedPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        _repo.GetDomainsAsync(1, Arg.Any<CancellationToken>()).Returns([new MailDomain { Id = 10, ServerId = 1, Name = "example.com" }]);
        _repo.GetDomainAsync(1, 10, Arg.Any<CancellationToken>()).Returns(new MailDomain { Id = 10, ServerId = 1, Name = "example.com" });

        await _sut.CreateDomainAsync(1, new CreateMailDomainRequest { Name = "example.org", DkimSelector = "mail" }, ct);
        await _sut.DeleteDomainAsync(1, 10, ct);
        await _sut.CreateAccountAsync(1, new CreateMailAccountRequest { Email = "user@example.com", Password = "secure-password", QuotaMb = 1024 }, ct);
        await _sut.ExecuteActionAsync(1, new MailActionRequest { Action = MailAction.RestartSpamFilter }, ct);

        Assert.Equal(4, _names.Count);
        Assert.StartsWith(ServerTaskNames.MailAddDomainPrefix, _names[0], StringComparison.Ordinal);
        Assert.StartsWith(ServerTaskNames.MailRemoveDomainPrefix, _names[1], StringComparison.Ordinal);
        Assert.StartsWith(ServerTaskNames.MailAddAccountPrefix, _names[2], StringComparison.Ordinal);
        Assert.Equal(ServerTaskNames.MailActionTask(MailAction.RestartSpamFilter), _names[3]);
        Assert.All(_names, name => Assert.True(IsAdopted(name), $"'{name}' is an action of the mail page but no adopted prefix matches it."));
    }

    [Fact]
    public void TheTasksOtherComponentsFollow_AreNeverAdoptedByThePage()
    {
        Assert.False(IsAdopted($"{MailTaskNames.SetupPrefix} (example.com)"));
        Assert.False(IsAdopted(MailTaskNames.LeftoversCheck));
        Assert.False(IsAdopted(ServerTaskNames.MailPurge));
        // Reads and the actions of other tabs are not the page's either.
        Assert.False(IsAdopted("Mail - mailbox usage"));
        Assert.False(IsAdopted("Mail logs - mail"));
        Assert.False(IsAdopted("Mail - read DKIM key (example.com)"));
    }
}
