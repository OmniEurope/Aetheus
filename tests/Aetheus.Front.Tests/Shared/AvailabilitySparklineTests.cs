// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Front.Tests.Architecture;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>S-UX-SPKL: the availability sparkline renders one bar per sample, coloured up/down.</summary>
public sealed class AvailabilitySparklineTests : BunitContext
{
    public AvailabilitySparklineTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void RendersBar_PerSample_ColouredByStatus()
    {
        var samples = new List<AppHealthSampleDto>
        {
            new() { Timestamp = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc), IsUp = true },
            new() { Timestamp = new DateTime(2026, 1, 10, 11, 0, 0, DateTimeKind.Utc), IsUp = false },
        };

        var cut = Render<AvailabilitySparkline>(p => p.Add(c => c.Samples, samples));

        var bars = cut.FindAll("rect");
        Assert.Equal(2, bars.Count);
        Assert.Contains("availability-sparkline-bar--up", bars[0].ClassName);
        Assert.Contains("availability-sparkline-bar--down", bars[1].ClassName);
        Assert.Null(bars[0].GetAttribute("fill"));
        Assert.Equal("Uptime24hLegend", cut.Find("svg > title").TextContent);
    }

    /// <summary>Availability-bar classes resolve to OE status tokens.</summary>
    [Theory]
    [InlineData("availability-sparkline-bar--up", "--omni-color-success")]
    [InlineData("availability-sparkline-bar--down", "--omni-color-danger")]
    public void BarClass_FillsWithTheOeStatusToken(string cssClass, string token)
    {
        var css = File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        var rule = Regex.Match(css, @"\." + Regex.Escape(cssClass) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(rule.Success, $"app.css has no .{cssClass} rule");
        Assert.Contains($"fill: var({token})", rule.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersPlaceholder_WhenNoSamples()
    {
        var cut = Render<AvailabilitySparkline>(p => p.Add(c => c.Samples, new List<AppHealthSampleDto>()));

        Assert.Empty(cut.FindAll("rect"));
        Assert.Contains("NotAvailable", cut.Markup); // stub localizer echoes the key
    }

    [Fact]
    public void DownSampledWindow_RemainsRedWhenAnyProbeFailed()
    {
        var samples = Enumerable.Range(0, 96).Select(index => new AppHealthSampleDto
        {
            Timestamp = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index),
            IsUp = index != 1
        }).ToList();

        var cut = Render<AvailabilitySparkline>(parameters => parameters.Add(component => component.Samples, samples));

        Assert.Equal(48, cut.FindAll("rect").Count);
        Assert.Single(cut.FindAll("rect.availability-sparkline-bar--down"));
    }
}
