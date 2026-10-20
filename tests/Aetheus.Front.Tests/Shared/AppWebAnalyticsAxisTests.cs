// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Tests.Shared;

/// <summary>The audience chart's value axis tops out on a round count.</summary>
public sealed class AppWebAnalyticsAxisTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(3, 5)]
    [InlineData(7, 10)]
    [InlineData(101, 200)]
    [InlineData(200, 200)]
    [InlineData(430, 500)]
    [InlineData(612, 1000)]
    public void RoundedAxisMaximum_IsOneTwoOrFiveTimesAPowerOfTenAtOrAboveThePeak(double peak, double expected) =>
        Assert.Equal(expected, AppWebAnalyticsView.RoundedAxisMaximum(peak));
}
