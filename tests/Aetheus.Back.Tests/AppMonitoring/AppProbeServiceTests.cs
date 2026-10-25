// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

public sealed class AppProbeServiceTests
{
    private readonly IAppMonitoringRepository _repo = Substitute.For<IAppMonitoringRepository>();
    private readonly IAppMonitoringService _monitoring = Substitute.For<IAppMonitoringService>();
    private readonly IHttpClientFactory _http = Substitute.For<IHttpClientFactory>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ProbeDueAppsAsync_NoConfiguredApps_DoesNotCreateHttpTrafficOrIngest()
    {
        _repo.GetBackendProbedAppsAsync(Arg.Any<CancellationToken>()).Returns([]);

        await Build().ProbeDueAppsAsync(TestContext.Current.CancellationToken);

        _http.DidNotReceive().CreateClient(Arg.Any<string>());
        await _monitoring.DidNotReceive().IngestProbeResultsAsync(
            Arg.Any<IReadOnlyCollection<AppProbeResultDto>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeDueAppsAsync_ProbesOnlyDueUrlsAndIngestsSuccessAndUnexpectedStatus()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _repo.GetBackendProbedAppsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MonitoredApp { Id = 1, ProbeUrl = "https://public.example/up", ExpectedStatusCode = 204, ProbeTimeoutSeconds = 5, ProbeIntervalSeconds = 30 },
            new MonitoredApp { Id = 2, ProbeUrl = "https://public.example/down", ExpectedStatusCode = 200, ProbeTimeoutSeconds = 5, ProbeIntervalSeconds = 30, LastCheckedAt = now.AddMinutes(-1) },
            new MonitoredApp { Id = 3, ProbeUrl = null, ExpectedStatusCode = 200, ProbeTimeoutSeconds = 5, ProbeIntervalSeconds = 30 },
            new MonitoredApp { Id = 4, ProbeUrl = "https://public.example/not-due", ExpectedStatusCode = 200, ProbeTimeoutSeconds = 5, ProbeIntervalSeconds = 300, LastCheckedAt = now }
        ]);
        _http.CreateClient(AppProbeService.ProbeHttpClientName)
            .Returns(_ => new HttpClient(new RouteStatusHandler()));

        await Build().ProbeDueAppsAsync(TestContext.Current.CancellationToken);

        await _monitoring.Received(1).IngestProbeResultsAsync(
            Arg.Is<IReadOnlyCollection<AppProbeResultDto>>(results =>
                results.Count == 2
                && results.Any(x => x.MonitoredAppId == 1 && x.IsUp && x.StatusCode == 204 && x.Error == null)
                && results.Any(x => x.MonitoredAppId == 2 && !x.IsUp && x.StatusCode == 503
                    && x.Error != null && x.Error.Contains("expected 200", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
        _http.Received(2).CreateClient(AppProbeService.ProbeHttpClientName);
    }

    [Fact]
    public async Task ProbeDueAppsAsync_HttpFailure_BecomesBoundedObservableDownResult()
    {
        _repo.GetBackendProbedAppsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MonitoredApp
            {
                Id = 8, ProbeUrl = "https://public.example/error", ExpectedStatusCode = 200,
                ProbeTimeoutSeconds = 5, ProbeIntervalSeconds = 30
            }
        ]);
        var longReason = new string('x', 700);
        _http.CreateClient(Arg.Any<string>())
            .Returns(new HttpClient(new ThrowingHandler(new HttpRequestException(longReason))));

        await Build().ProbeDueAppsAsync(TestContext.Current.CancellationToken);

        await _monitoring.Received(1).IngestProbeResultsAsync(
            Arg.Is<IReadOnlyCollection<AppProbeResultDto>>(results =>
                results.Single().MonitoredAppId == 8 && !results.Single().IsUp
                && results.Single().StatusCode == null && results.Single().Error!.Length == 500),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HostedService_DisabledConfiguration_StopsWithoutOpeningScope()
    {
        var provider = new ServiceCollection()
            .AddSingleton(_repo)
            .AddSingleton(_monitoring)
            .BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AppMonitoring:BackendProbe:Enabled"] = "false" }).Build();
        var service = new AppProbeService(
            provider.GetRequiredService<IServiceScopeFactory>(), _http, config,
            NullLogger<AppProbeService>.Instance, _time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().GetBackendProbedAppsAsync(Arg.Any<CancellationToken>());
    }

    private AppProbeService Build()
    {
        var provider = new ServiceCollection()
            .AddSingleton(_repo)
            .AddSingleton(_monitoring)
            .BuildServiceProvider();
        return new AppProbeService(
            provider.GetRequiredService<IServiceScopeFactory>(), _http,
            new ConfigurationBuilder().Build(), NullLogger<AppProbeService>.Instance, _time);
    }

    private sealed class RouteStatusHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = request.RequestUri!.AbsolutePath == "/up"
                ? System.Net.HttpStatusCode.NoContent
                : System.Net.HttpStatusCode.ServiceUnavailable;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(error);
    }
}
