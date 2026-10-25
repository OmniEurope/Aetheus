// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail;

/// <summary>Remembers when the no-reply password was last re-provisioned. One instance per process.</summary>
public sealed class NoReplyRepairThrottle(TimeProvider time)
{
    private readonly object _gate = new();
    private DateTimeOffset? _last;

    public bool TryAcquire(TimeSpan cooldown)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_last is { } last && now - last < cooldown)
                return false;
            _last = now;
            return true;
        }
    }
}
