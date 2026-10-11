// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.Mail.Mailbox;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>Recette R1-17: the typed mailbox passwords are held in memory, per user, for a bounded time.</summary>
public sealed class MailboxCredentialStoreTests
{
    private const string Password = "s3cret-mailbox-pass";
    private static readonly MailboxCredentialKey s_alice = new("Bearer:1", 7, 3);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
    private readonly MailboxCredentialStore _store;

    public MailboxCredentialStoreTests() => _store = new MailboxCredentialStore(_time);

    [Fact]
    public void A_stored_password_is_returned_with_its_idle_expiry()
    {
        var expiresAt = _store.Store(s_alice, Password);

        Assert.Equal(_time.GetUtcNow().UtcDateTime + MailboxCredentialStore.IdleTimeout, expiresAt);
        Assert.Equal(Password, _store.TryUse(s_alice));
        Assert.Equal(expiresAt, _store.ExpiresAt(s_alice));
    }

    [Fact]
    public void Another_user_with_the_same_mailbox_gets_nothing()
    {
        _store.Store(s_alice, Password);

        Assert.Null(_store.TryUse(s_alice with { UserId = "Bearer:2" }));
        Assert.Null(_store.TryUse(s_alice with { AccountId = 4 }));
        Assert.Null(_store.TryUse(s_alice with { ServerId = 8 }));
        Assert.Null(_store.ExpiresAt(s_alice with { UserId = "Bearer:2" }));
    }

    [Fact]
    public void The_password_is_forgotten_after_the_idle_timeout()
    {
        _store.Store(s_alice, Password);

        _time.Advance(MailboxCredentialStore.IdleTimeout);

        Assert.Null(_store.TryUse(s_alice));
        Assert.Null(_store.ExpiresAt(s_alice));
    }

    [Fact]
    public void Use_extends_the_idle_window_but_not_past_the_maximum_lifetime()
    {
        _store.Store(s_alice, Password);
        var step = MailboxCredentialStore.IdleTimeout - TimeSpan.FromMinutes(1);
        var elapsed = TimeSpan.Zero;
        while (elapsed + step < MailboxCredentialStore.MaxLifetime)
        {
            _time.Advance(step);
            elapsed += step;
            Assert.Equal(Password, _store.TryUse(s_alice));
        }

        _time.Advance(MailboxCredentialStore.MaxLifetime - elapsed);

        Assert.Null(_store.TryUse(s_alice));
    }

    [Fact]
    public void ExpiresAt_does_not_extend_the_window()
    {
        _store.Store(s_alice, Password);
        _time.Advance(TimeSpan.FromMinutes(20));
        _ = _store.ExpiresAt(s_alice);
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Null(_store.TryUse(s_alice));
    }

    [Fact]
    public void Lock_forgets_the_password_of_that_user_only()
    {
        var bob = s_alice with { UserId = "Bearer:2" };
        _store.Store(s_alice, Password);
        _store.Store(bob, "bob-password");

        _store.Forget(s_alice);

        Assert.Null(_store.TryUse(s_alice));
        Assert.Equal("bob-password", _store.TryUse(bob));
    }

    [Fact]
    public void The_store_is_bounded_and_drops_the_oldest_entry()
    {
        for (var index = 0; index < MailboxCredentialStore.MaxEntries; index++)
        {
            _store.Store(s_alice with { AccountId = index }, Password);
            _time.Advance(TimeSpan.FromMilliseconds(1));
        }

        _store.Store(s_alice with { AccountId = -1 }, Password);

        Assert.Null(_store.TryUse(s_alice with { AccountId = 0 }));
        Assert.Equal(Password, _store.TryUse(s_alice with { AccountId = 1 }));
        Assert.Equal(Password, _store.TryUse(s_alice with { AccountId = -1 }));
    }

    [Fact]
    public void An_empty_user_or_password_is_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => _store.Store(s_alice with { UserId = string.Empty }, Password));
        Assert.ThrowsAny<ArgumentException>(() => _store.Store(s_alice, string.Empty));
    }

    [Fact]
    public void No_mailbox_dto_has_a_property_that_could_carry_a_password_back()
    {
        var outbound = new[]
        {
            typeof(MailboxAccessDto), typeof(MailClientSettingsDto), typeof(MailboxFolderDto), typeof(MailboxMessageSummaryDto),
            typeof(MailboxMessageDto), typeof(MailboxDraftDto), typeof(MailboxSendResultDto), typeof(MailboxDeleteResultDto),
            typeof(MailboxAttachmentDto)
        };

        foreach (var type in outbound)
        {
            var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name);
            Assert.DoesNotContain(names, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_endpoint_record_keeps_the_password_out_of_its_text()
    {
        var endpoint = new MailboxEndpoint("mail.example.org", 993, 587, "me@example.org", Password);

        Assert.DoesNotContain(Password, endpoint.ToString(), StringComparison.Ordinal);
    }
}
