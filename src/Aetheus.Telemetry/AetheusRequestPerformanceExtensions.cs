// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OmniEurope.Performance;

namespace Aetheus.Telemetry;

internal static class AetheusRequestPerformanceExtensions
{
    /// <summary>
    /// Decision of 2026-10-07: the requests are measured by the collector of OmniEurope.Performance, the
    /// package the host may also use for a page of its own; this package only exports what it measured, as
    /// the per-route gauges of <see cref="RequestPerformanceMetrics"/>. Safe to call more than once.
    ///
    /// The 24-hour window is a default, not a decision taken for the host: it is registered only when no
    /// one registered the collector before, and ahead of the collector's own configuration binding, so
    /// <c>OmniPerformance:Window</c> and every later <c>AddOmniPerformanceCollector</c> delegate still win.
    /// A host that registered the collector first keeps the window it chose.
    /// </summary>
    internal static IServiceCollection AddAetheusRequestPerformanceMetrics(this IServiceCollection services)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(RequestPerformanceRecorder)))
            services.Configure<PerformanceOptions>(options => options.Window = RequestPerformanceMetrics.DefaultWindow);
        services.AddOmniPerformanceCollector();
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RequestPerformanceMetrics)))
            return services;

        services.TryAddSingleton(provider => new RequestPerformanceMetrics(
            provider.GetRequiredService<RequestPerformanceRecorder>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<RequestPerformanceMetrics>());
        return services;
    }
}
