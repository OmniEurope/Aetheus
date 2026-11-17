// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary>R-17: a slow preparation or launch reaches the file log the UI reads (Warning), a fast one does not.</summary>
public sealed class PhaseTimingLevelTests
{
    private static IConfiguration Config(string? thresholdMs = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(thresholdMs is null
            ? []
            : new Dictionary<string, string?> { ["SlowRequestDiagnostics:Threshold"] = thresholdMs })
        .Build();

    private static PhaseTiming After(TimeSpan elapsed)
    {
        var clock = new FakeTimeProvider();
        var timing = new PhaseTiming(clock);
        clock.Advance(elapsed);
        timing.Mark("phase");
        return timing;
    }

    [Fact]
    public void An_operation_at_the_default_threshold_logs_a_warning() =>
        Assert.Equal(LogLevel.Warning, After(TimeSpan.FromMilliseconds(1500)).LevelFor(Config()));

    [Fact]
    public void An_operation_under_the_default_threshold_logs_information() =>
        Assert.Equal(LogLevel.Information, After(TimeSpan.FromMilliseconds(1499)).LevelFor(Config()));

    [Fact]
    public void The_configured_slow_request_threshold_is_the_one_used() =>
        Assert.Equal(LogLevel.Warning, After(TimeSpan.FromSeconds(1)).LevelFor(Config("00:00:00.500")));

    [Fact]
    public void A_disabled_threshold_never_warns() =>
        Assert.Equal(LogLevel.Information, After(TimeSpan.FromMinutes(5)).LevelFor(Config("00:00:00")));
}
