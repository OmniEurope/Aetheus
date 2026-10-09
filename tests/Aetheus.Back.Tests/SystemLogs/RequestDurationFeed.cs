// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OmniEurope.Performance;

namespace Aetheus.Back.Tests.SystemLogs;

/// <summary>
/// Feeds an OmniEurope.Performance recorder the way ASP.NET Core does: the <c>http.server.request.duration</c>
/// histogram of the <c>Microsoft.AspNetCore.Hosting</c> meter, created by the recorder's own meter factory
/// (the recorder hears no other). A request is one measurement, in seconds, with the route template, the
/// method and the status code as tags.
/// </summary>
internal sealed class RequestDurationFeed : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly Histogram<double> _duration;

    public RequestDurationFeed(Action<PerformanceOptions> configure, TimeProvider time)
    {
        _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = _provider.GetRequiredService<IMeterFactory>();
        var options = new PerformanceOptions();
        configure(options);
        Recorder = new RequestPerformanceRecorder(Options.Create(options), time, meters);
        Recorder.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _duration = meters.Create(RequestPerformanceRecorder.MeterName)
            .CreateHistogram<double>(RequestPerformanceRecorder.InstrumentName);
    }

    public RequestPerformanceRecorder Recorder { get; }

    public void Request(string? route, string method, int statusCode, double milliseconds) =>
        _duration.Record(milliseconds / 1000,
            new KeyValuePair<string, object?>("http.route", route),
            new KeyValuePair<string, object?>("http.request.method", method),
            new KeyValuePair<string, object?>("http.response.status_code", statusCode));

    public void Dispose()
    {
        Recorder.Dispose();
        _provider.Dispose();
    }
}
