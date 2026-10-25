// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>A refused ingestion key is one warning per key and period, not one per
/// request.</summary>
public sealed class IngestKeyRejectionLogTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 5, 21, 0, TimeSpan.Zero));
    private readonly RecordingLogger<IngestKeyRejectionLog> _log = new();

    private IngestKeyRejectionLog Log() => new(_clock, _log);

    [Fact]
    public void TheFirstRefusal_IsAWarningAtOnce_TheRepeatsOfThePeriodAreNot()
    {
        var log = Log();

        for (var i = 0; i < 9; i++)
        {
            log.Record("STALEKEYHASH-0123456789abcdef");
            _clock.Advance(TimeSpan.FromSeconds(20));
        }

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("STALEKEYHASH", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterThePeriod_TheNextRefusal_ReportsHowManyWereNotWritten()
    {
        var log = Log();
        log.Record("hash-a");
        log.Record("hash-a");
        log.Record("hash-a");

        _clock.Advance(IngestKeyRejectionLog.Period);
        log.Record("hash-a");

        Assert.Equal(2, _log.Entries.Count);
        Assert.All(_log.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.Contains(" 3 more time(s) ", _log.Entries[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuietKey_StartsAFreshPeriod_WithoutACount()
    {
        var log = Log();
        log.Record("hash-a");
        _clock.Advance(IngestKeyRejectionLog.Period * 2);
        log.Record("hash-a");

        Assert.Equal(2, _log.Entries.Count);
        Assert.DoesNotContain("more time(s)", _log.Entries[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoKeys_AndAMissingKey_AreReportedSeparately()
    {
        var log = Log();
        log.Record("hash-a");
        log.Record("hash-b");
        log.Record(null);
        log.Record(null);

        Assert.Equal(3, _log.Entries.Count);
        Assert.Contains(IngestKeyRejectionLog.MissingKey, _log.Entries[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BeyondTheTrackedKeys_RefusalsShareOneEntry_SoRandomKeysCannotFloodTheWarnings()
    {
        var log = Log();
        for (var i = 0; i < IngestKeyRejectionLog.MaxTrackedKeys + 50; i++)
            log.Record($"{i:D6}-random-key-hash");

        Assert.Equal(IngestKeyRejectionLog.MaxTrackedKeys + 1, _log.Entries.Count);
        Assert.Contains(IngestKeyRejectionLog.OtherKeys, _log.Entries[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarning_NamesTheOriginOfTheSender()
    {
        Log().Record("hash-a", "203.0.113.9 (user-agent \"OTel-OTLP-Exporter\", path /v1/metrics)");

        var message = Assert.Single(_log.Entries).Message;
        Assert.Contains("203.0.113.9", message, StringComparison.Ordinal);
        Assert.Contains("OTel-OTLP-Exporter", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSendersHoldingTheSameStaleKey_AreReportedSeparately()
    {
        var log = Log();
        log.Record("hash-a", "198.51.100.1");
        log.Record("hash-a", "198.51.100.2");
        log.Record("hash-a", "198.51.100.1");

        Assert.Equal(2, _log.Entries.Count);
        Assert.Contains("198.51.100.1", _log.Entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("198.51.100.2", _log.Entries[1].Message, StringComparison.Ordinal);
    }
}
