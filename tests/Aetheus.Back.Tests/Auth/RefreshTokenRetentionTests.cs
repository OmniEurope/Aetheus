// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;

namespace Aetheus.Back.Tests.Auth;

public sealed class RefreshTokenRetentionTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RevokedRetention_IsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), RefreshTokenRetention.RevokedRetention);
    }

    [Fact]
    public void RevokedCutoff_IsNowMinusTheRetention()
    {
        Assert.Equal(Now.AddMinutes(-5), RefreshTokenRetention.RevokedCutoff(Now));
    }

    [Fact]
    public void SweepInterval_IsNotLongerThanTheRetention_SoARevokedRowOutlivesItByLessThanOneSweep()
    {
        Assert.True(RefreshTokenRetention.SweepInterval <= RefreshTokenRetention.RevokedRetention);
    }
}
