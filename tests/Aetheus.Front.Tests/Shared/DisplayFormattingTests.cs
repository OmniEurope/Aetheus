// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Front.Components.Shared;

namespace Aetheus.Front.Tests.Shared;

/// <summary>The one duration format (R-23) and the one size format (R-37, R-47) of the site.</summary>
public sealed class DisplayFormattingTests
{
    /// <summary>Header and menu counters stop at "9+" (user decision of 2026-10-24).</summary>
    [Theory]
    [InlineData(1, "1")]
    [InlineData(9, "9")]
    [InlineData(10, "9+")]
    [InlineData(250, "9+")]
    public void BadgeCount_ShowsOneToNine_ThenNinePlus(int count, string shown) =>
        Assert.Equal(shown, DisplayFormatting.BadgeCount(count));

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(42, "42s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1min")]
    [InlineData(75, "1min")]
    [InlineData(2_532, "42min")]
    [InlineData(3_599, "59min")]
    [InlineData(3_600, "1h00min")]
    [InlineData(6_260, "1h44min")]
    [InlineData(90_000, "25h00min")]
    public void Duration_SecondsUnderAMinute_MinutesUnderAnHour_HoursBeyond(int totalSeconds, string expected) =>
        Assert.Equal(expected, DisplayFormatting.Duration(TimeSpan.FromSeconds(totalSeconds)));

    [Fact]
    public void Duration_ANegativeSpanReadsZero() =>
        Assert.Equal("0s", DisplayFormatting.Duration(TimeSpan.FromSeconds(-5)));

    [Fact]
    public void Duration_ARunOf104Minutes20Seconds_IsNotWrittenInMinutesBeyondTheHour() =>
        Assert.Equal("1h44min", DisplayFormatting.Duration(new TimeSpan(0, 104, 20)));

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(42, "42s")]
    [InlineData(60, "1min00s")]
    [InlineData(90, "1min30s")]
    [InlineData(125, "2min05s")]
    [InlineData(599, "9min59s")]
    [InlineData(600, "10min")]
    [InlineData(6_260, "1h44min")]
    public void PreciseDuration_KeepsTheSecondsBelowTenMinutes_ThenFollowsDuration(int totalSeconds, string expected) =>
        Assert.Equal(expected, DisplayFormatting.PreciseDuration(TimeSpan.FromSeconds(totalSeconds)));

    [Fact]
    public void PreciseDuration_ANegativeSpanReadsZero() =>
        Assert.Equal("0s", DisplayFormatting.PreciseDuration(TimeSpan.FromSeconds(-5)));

    [Theory]
    [InlineData(0L, "0 o")]
    [InlineData(999L, "999 o")]
    [InlineData(1_024L, "1 Ko")]
    [InlineData(1_536L, "1,5 Ko")]
    [InlineData(1_048_576L, "1 Mo")]
    [InlineData(512L * 1_048_576, "512 Mo")]
    [InlineData(1_073_741_823L, "1024 Mo")]
    [InlineData(1_073_741_824L, "1 Go")]
    [InlineData(3_931L * 1_048_576, "3,8 Go")]
    [InlineData(40L * 1_073_741_824, "40 Go")]
    public void Size_UsesFrenchUnits_AndGoFromOneGigabyte(long bytes, string expected)
    {
        using var _ = new CultureScope("fr-FR");
        Assert.Equal(expected, DisplayFormatting.Size(bytes));
    }

    [Fact]
    public void Size_NeverWritesABinaryOrEnglishUnitName()
    {
        using var _ = new CultureScope("en-US");
        foreach (var bytes in new[] { 10L, 10_000L, 10_000_000L, 10_000_000_000L })
        {
            var text = DisplayFormatting.Size(bytes);
            Assert.DoesNotContain("iB", text, StringComparison.Ordinal);
            Assert.DoesNotContain("KB", text, StringComparison.Ordinal);
            Assert.DoesNotContain("MB", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GB", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Size_ANegativeSizeReadsZero()
    {
        using var _ = new CultureScope("fr-FR");
        Assert.Equal("0 o", DisplayFormatting.Size(-12));
    }

    [Theory]
    [InlineData(0d, "0 o")]
    [InlineData(0.04, "41 Mo")]
    [InlineData(396.8, "396,8 Go")]
    [InlineData(796.2, "796,2 Go")]
    [InlineData(-3d, "0 o")]
    [InlineData(double.NaN, "0 o")]
    public void SizeFromGo_FormatsTheAgentDiskFigures(double gigabytes, string expected)
    {
        using var _ = new CultureScope("fr-FR");
        Assert.Equal(expected, DisplayFormatting.SizeFromGo(gigabytes));
    }

    [Theory]
    [InlineData(5_324.8, "5,2 Go")]
    [InlineData(16_000d, "15,6 Go")]
    [InlineData(740d, "740 Mo")]
    [InlineData(0d, "0 o")]
    public void SizeFromMo_FormatsTheAgentMemoryFigures(double megabytes, string expected)
    {
        using var _ = new CultureScope("fr-FR");
        Assert.Equal(expected, DisplayFormatting.SizeFromMo(megabytes));
    }

    [Theory]
    [InlineData("182MB", "173,6 Mo")]
    [InlineData("1.2GB", "1,1 Go")]
    [InlineData("512kB", "500 Ko")]
    [InlineData("0B", "0 o")]
    [InlineData("2GiB", "2 Go")]
    [InlineData("34.0 GiB", "34 Go")]
    [InlineData("", "")]
    [InlineData("n/a", "n/a")]
    [InlineData("12 parsecs", "12 parsecs")]
    public void DockerSize_RewritesTheDockerCliTextInTheSiteFormat(string docker, string expected)
    {
        using var _ = new CultureScope("fr-FR");
        Assert.Equal(expected, DisplayFormatting.DockerSize(docker));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
