// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// The wire DateTime.MinValue / MaxValue are sentinels ("never collected", "no limit"), not instants.
/// Localising them produced "1/1/0001 12:10 AM" and broke every <c>== default</c> guard on a DTO
/// (SITE-FUNC-STORAGE-DATE-001). A real instant must still be localised.
/// </summary>
public sealed class UtcToLocalDateTimeConverterTests
{
    private sealed record Probe(DateTime At, DateTime? Maybe);

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("0001-01-01T00:00:00")]
    public void WireMinValue_StaysDefault_InBothConverters(string wire)
    {
        var probe = JsonSerializer.Deserialize<Probe>($$"""{"at":"{{wire}}","maybe":"{{wire}}"}""", JsonOptions.Web)!;

        Assert.Equal(default, probe.At);
        Assert.Equal(DateTime.MinValue, probe.Maybe);
    }

    [Theory]
    [InlineData("9999-12-31T23:59:59.9999999Z")]
    [InlineData("9999-12-31T23:59:59.9999999")]
    public void WireMaxValue_StaysMaxValue_InBothConverters(string wire)
    {
        var probe = JsonSerializer.Deserialize<Probe>($$"""{"at":"{{wire}}","maybe":"{{wire}}"}""", JsonOptions.Web)!;

        Assert.Equal(DateTime.MaxValue, probe.At);
        Assert.Equal(DateTime.MaxValue, probe.Maybe);
    }

    [Fact]
    public void RealInstant_IsStillLocalised()
    {
        var utc = new DateTime(2026, 7, 15, 10, 30, 0, DateTimeKind.Utc);

        var probe = JsonSerializer.Deserialize<Probe>("""{"at":"2026-07-15T10:30:00Z","maybe":"2026-07-15T10:30:00"}""", JsonOptions.Web)!;

        Assert.Equal(utc.ToLocalTime(), probe.At);
        Assert.Equal(DateTimeKind.Local, probe.At.Kind);
        Assert.Equal(utc.ToLocalTime(), probe.Maybe);
    }

    [Fact]
    public void Sentinels_AreWrittenUnshifted()
    {
        var json = JsonSerializer.Serialize(new Probe(DateTime.MinValue, DateTime.MaxValue), JsonOptions.Web);

        Assert.Contains("\"at\":\"0001-01-01T00:00:00Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maybe\":\"9999-12-31T23:59:59.9999999Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalSentinel_RoundTrips()
    {
        var local = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Local);

        var json = JsonSerializer.Serialize(new Probe(local, null), JsonOptions.Web);
        var back = JsonSerializer.Deserialize<Probe>(json, JsonOptions.Web)!;

        Assert.Equal(default, back.At);
        Assert.Null(back.Maybe);
    }
}
