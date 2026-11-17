// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// A hub method that refuses on purpose (<see cref="HubException"/>) must be told at Information, not as
/// the Error SignalR's dispatcher writes for every hub exception; any other exception stays an Error. Real
/// SignalR client, real hub pipeline, real <c>JsonFileLoggerProvider</c> reading the lines it writes to disk.
/// </summary>
public sealed class HubRefusalLoggingIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string DispatcherCategory = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";
    private const int CrashingRunId = int.MaxValue;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeliberateRefusal_ReachesTheClient_AndIsLoggedAtInformationOnly()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "aetheus-hub-log", Guid.NewGuid().ToString("N"));
        List<LogLine> lines;
        try
        {
            await using (var factory = new AetheusWebApplicationFactory(fixture.ConnectionString))
            await using (var host = HostWithLogDirectory(factory, logDirectory))
            {
                using var client = host.CreateClient();
                var token = await IntegrationAuth.LoginAsAdminAsync(client, Ct);
                await using var hub = BuildHubConnection(host, token);
                await hub.StartAsync(Ct);

                var refusal = await Assert.ThrowsAsync<HubException>(
                    () => hub.InvokeAsync("JoinPipelineRunGroup", 987654, Ct));
                Assert.Contains("Run not found.", refusal.Message);

                // A different exception follows: the logger is one FIFO writer, so once its Error line is on
                // disk every earlier line of this connection is too, and absence checks below are meaningful.
                await Assert.ThrowsAsync<HubException>(
                    () => hub.InvokeAsync("JoinPipelineRunGroup", CrashingRunId, Ct));
                lines = await ReadLinesUntilAsync(logDirectory, l => l.Level == "Error" && l.Category == DispatcherCategory);
            }
        }
        finally
        {
            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true);
        }

        var refused = lines.Where(l => l.Message.Contains("Run not found.", StringComparison.Ordinal)).ToList();
        var information = Assert.Single(refused, l => l.Level == "Information");
        Assert.Equal("Aetheus.Back.Hubs.HubRefusalLogFilter", information.Category);
        Assert.Contains("PipelineHub.JoinPipelineRunGroup", information.Message);
        Assert.DoesNotContain(refused, l => l.Level == "Error");
        Assert.DoesNotContain(lines, l => l.Level == "Error" && l.Exception.Contains("Run not found.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherHubException_StaysAnErrorLine()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "aetheus-hub-log", Guid.NewGuid().ToString("N"));
        List<LogLine> lines;
        try
        {
            await using (var factory = new AetheusWebApplicationFactory(fixture.ConnectionString))
            await using (var host = HostWithLogDirectory(factory, logDirectory))
            {
                using var client = host.CreateClient();
                var token = await IntegrationAuth.LoginAsAdminAsync(client, Ct);
                await using var hub = BuildHubConnection(host, token);
                await hub.StartAsync(Ct);

                await Assert.ThrowsAsync<HubException>(
                    () => hub.InvokeAsync("JoinPipelineRunGroup", CrashingRunId, Ct));
                lines = await ReadLinesUntilAsync(logDirectory, l => l.Level == "Error" && l.Category == DispatcherCategory);
            }
        }
        finally
        {
            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true);
        }

        var error = Assert.Single(lines, l => l.Level == "Error" && l.Category == DispatcherCategory);
        Assert.Contains("Failed to invoke hub method 'JoinPipelineRunGroup'", error.Message);
        Assert.Contains("InvalidOperationException", error.Exception);
        Assert.DoesNotContain(lines, l => l.Category == "Aetheus.Back.Hubs.HubRefusalLogFilter");
    }

    private static WebApplicationFactory<Program> HostWithLogDirectory(AetheusWebApplicationFactory factory, string logDirectory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Logging:FileLog:Directory", logDirectory);
            builder.UseSetting("Logging:FileLog:MinLevel", "Information");
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IPipelineRunLookupReader, CrashingRunLookup>());
        });

    private static HubConnection BuildHubConnection(WebApplicationFactory<Program> host, string token) =>
        new HubConnectionBuilder()
            .WithUrl($"{host.Server.BaseAddress}hubs/pipelines", options =>
            {
                options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private static async Task<List<LogLine>> ReadLinesUntilAsync(string directory, Func<LogLine, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var lines = Directory.Exists(directory) ? ReadLines(directory) : [];
            if (lines.Any(until)) return lines;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The expected log line never reached the log file.");
            await Task.Delay(100, Ct);
        }
    }

    private static List<LogLine> ReadLines(string directory)
    {
        var result = new List<LogLine>();
        foreach (var file in Directory.GetFiles(directory, "aetheus-*.json"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { Length: > 0 } text)
            {
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                result.Add(new LogLine(
                    root.GetProperty("Level").GetString() ?? "",
                    root.GetProperty("SourceContext").GetString() ?? "",
                    root.GetProperty("Message").GetString() ?? "",
                    root.TryGetProperty("Exception", out var exception) ? exception.GetString() ?? "" : ""));
            }
        }
        return result;
    }

    private sealed record LogLine(string Level, string Category, string Message, string Exception);

    /// <summary>Unknown run for every id but one; <see cref="CrashingRunId"/> fails with a non-hub exception.</summary>
    private sealed class CrashingRunLookup : IPipelineRunLookupReader
    {
        public Task<int?> GetPipelineIdForRunAsync(int runId, CancellationToken ct = default) =>
            runId == CrashingRunId
                ? throw new InvalidOperationException("run lookup crashed")
                : Task.FromResult<int?>(null);

        public Task<Dictionary<int, PipelineStatus>> GetRunStatusesByIdsAsync(IReadOnlyCollection<int> runIds, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<int, PipelineStatus>());

        public Task<PipelineRunDeployedReleaseDto?> GetDeployedReleaseAsync(
            int runId, bool includeAlreadyDeployed, CancellationToken ct = default) =>
            Task.FromResult<PipelineRunDeployedReleaseDto?>(null);
    }
}
