// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary> The end of an Aetheus user's session forgets the mailbox passwords that user holds, at
/// once, and only theirs. Driven through the real in-process dispatcher, wired the way the Mail
/// module wires it. </summary>
public sealed class MailboxCredentialSessionEndTests
{
    private const string Password = "s3cret-mailbox-pass";
    private const string JwtScheme = "AuthenticationTypes.Federation";

    private static readonly MailboxCredentialKey s_aliceFirst = new(MailboxCredentialKey.Holder(JwtScheme, "1"), 7, 3);
    private static readonly MailboxCredentialKey s_aliceSecond = new(MailboxCredentialKey.Holder(JwtScheme, "1"), 8, 4);
    private static readonly MailboxCredentialKey s_aliceByPat = new(MailboxCredentialKey.Holder(PatConstants.SchemeName, "1"), 7, 5);
    private static readonly MailboxCredentialKey s_bob = new(MailboxCredentialKey.Holder(JwtScheme, "2"), 7, 3);
    private static readonly MailboxCredentialKey s_user11 = new(MailboxCredentialKey.Holder(JwtScheme, "11"), 7, 3);
    private static readonly MailboxCredentialKey s_agentOfServer1 =
        new(MailboxCredentialKey.Holder(AgentTokenAuthenticationHandler.SchemeName, "1"), 7, 3);

    private readonly MailboxCredentialStore _store = new(new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero)));

    private IDomainEventDispatcher Dispatcher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMailboxCredentialStore>(_store);
        services.AddSingleton<ILogger<MailboxCredentialSessionEndHandler>>(NullLogger<MailboxCredentialSessionEndHandler>.Instance);
        services.AddScoped<IDomainEventHandler<UserSessionEndedEvent>, MailboxCredentialSessionEndHandler>();
        var scope = services.BuildServiceProvider().CreateScope();
        return new DomainEventDispatcher(scope.ServiceProvider, new BackgroundTaskQueue(), NullLogger<DomainEventDispatcher>.Instance);
    }

    private void StoreAll()
    {
        foreach (var key in new[] { s_aliceFirst, s_aliceSecond, s_aliceByPat, s_bob, s_user11, s_agentOfServer1 })
            _store.Store(key, Password);
    }

    [Theory]
    [InlineData(UserSessionEndReasons.Logout)]
    [InlineData(UserSessionEndReasons.RefreshReplay)]
    [InlineData(UserSessionEndReasons.PasswordChanged)]
    [InlineData(UserSessionEndReasons.Deactivated)]
    [InlineData(UserSessionEndReasons.Deleted)]
    public async Task The_end_of_a_session_forgets_that_users_passwords_and_keeps_the_others(string reason)
    {
        StoreAll();

        await Dispatcher().DispatchAsync(new UserSessionEndedEvent(1, reason), TestContext.Current.CancellationToken);

        Assert.Null(_store.TryUse(s_aliceFirst));
        Assert.Null(_store.TryUse(s_aliceSecond));
        Assert.Null(_store.TryUse(s_aliceByPat));
        Assert.Null(_store.ExpiresAt(s_aliceFirst));
        Assert.Equal(Password, _store.TryUse(s_bob));
        Assert.Equal(Password, _store.TryUse(s_user11));
        Assert.Equal(Password, _store.TryUse(s_agentOfServer1));
    }

    [Fact]
    public void ForgetUser_counts_what_it_forgot_and_a_second_call_finds_nothing()
    {
        StoreAll();

        Assert.Equal(3, _store.ForgetUser(1));
        Assert.Equal(0, _store.ForgetUser(1));
        Assert.Equal(1, _store.ForgetUser(2));
        Assert.Equal(Password, _store.TryUse(s_user11));
    }

    [Fact]
    public void A_key_belongs_to_the_user_whatever_the_scheme_but_never_to_an_agent()
    {
        Assert.True(s_aliceFirst.IsHeldBy(1));
        Assert.True(s_aliceByPat.IsHeldBy(1));
        Assert.False(s_user11.IsHeldBy(1));
        Assert.False(s_bob.IsHeldBy(1));
        Assert.False(s_agentOfServer1.IsHeldBy(1));
        Assert.False(new MailboxCredentialKey("1", 7, 3).IsHeldBy(1));
    }
}
