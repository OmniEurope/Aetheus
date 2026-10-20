// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace Aetheus.WebAnalytics;

internal sealed class AnalyticsExportQueue
{
    private const int Capacity = 2048;
    private static readonly Meter Meter = new("Aetheus.WebAnalytics", "1.1.0");
    private static readonly Counter<long> Accepted = Meter.CreateCounter<long>("aetheus.analytics.accepted");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("aetheus.analytics.dropped");
    private readonly Channel<AnalyticsExportEvent> _channel = Channel.CreateBounded<AnalyticsExportEvent>(
        new BoundedChannelOptions(Capacity)
        {
            // TryWrite remains non-blocking with Wait mode and returns false at capacity, which lets us
            // count every dropped event. DropWrite reports success even when it silently drops the item.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public bool TryWrite(AnalyticsExportEvent analyticsEvent)
    {
        if (_channel.Writer.TryWrite(analyticsEvent))
        {
            Accepted.Add(1);
            return true;
        }

        Dropped.Add(1, new KeyValuePair<string, object?>("reason", "queue_full"));
        return false;
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) =>
        _channel.Reader.WaitToReadAsync(ct);

    public bool TryRead(out AnalyticsExportEvent? analyticsEvent) =>
        _channel.Reader.TryRead(out analyticsEvent);
}
