// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>The in-memory, expiring <see cref="IMailboxCredentialStore"/>; a singleton.</summary>
public sealed class MailboxCredentialStore(TimeProvider timeProvider) : IMailboxCredentialStore
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(8);

    /// <summary>Upper bound of held passwords, so the store cannot grow without end; the oldest goes first.</summary>
    public const int MaxEntries = 500;

    private sealed class Entry(MailboxHeldCredential credential, DateTime storedAt, DateTime lastUsedAt)
    {
        public MailboxHeldCredential Credential { get; } = credential;
        public DateTime StoredAt { get; } = storedAt;
        public DateTime LastUsedAt { get; set; } = lastUsedAt;

        public DateTime ExpiresAt => Min(LastUsedAt + IdleTimeout, StoredAt + MaxLifetime);
    }

    private readonly Dictionary<MailboxCredentialKey, Entry> _entries = [];
    private readonly Lock _gate = new();

    public DateTime Store(MailboxCredentialKey key, string password, string? login = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key.UserId);
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (login is not null) ArgumentException.ThrowIfNullOrWhiteSpace(login);
        var now = Now();
        lock (_gate)
        {
            Purge(now);
            if (!_entries.ContainsKey(key) && _entries.Count >= MaxEntries)
                _entries.Remove(_entries.MinBy(pair => pair.Value.StoredAt).Key);
            var entry = new Entry(new MailboxHeldCredential(password, login), now, now);
            _entries[key] = entry;
            return entry.ExpiresAt;
        }
    }

    public string? TryUse(MailboxCredentialKey key) => TryUseCredential(key)?.Password;

    public MailboxHeldCredential? TryUseCredential(MailboxCredentialKey key)
    {
        var now = Now();
        lock (_gate)
        {
            Purge(now);
            if (!_entries.TryGetValue(key, out var entry)) return null;
            entry.LastUsedAt = now;
            return entry.Credential;
        }
    }

    public DateTime? ExpiresAt(MailboxCredentialKey key)
    {
        var now = Now();
        lock (_gate)
        {
            Purge(now);
            return _entries.TryGetValue(key, out var entry) ? entry.ExpiresAt : null;
        }
    }

    public bool IsAdministratorAccess(MailboxCredentialKey key)
    {
        var now = Now();
        lock (_gate)
        {
            Purge(now);
            return _entries.TryGetValue(key, out var entry) && entry.Credential.IsAdministratorAccess;
        }
    }

    public void Forget(MailboxCredentialKey key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    public int ForgetUser(int userId)
    {
        lock (_gate)
        {
            var held = _entries.Keys.Where(key => key.IsHeldBy(userId)).ToList();
            foreach (var key in held)
                _entries.Remove(key);
            return held.Count;
        }
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private void Purge(DateTime now)
    {
        foreach (var expired in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
            _entries.Remove(expired);
    }

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;
}
