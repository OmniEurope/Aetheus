// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.SystemLogs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R1-89, decision of 2026-10-14: a restored realtime outage is written to the system log as a
/// warning, a report without a level stays an error (clients that predate the field), and a level outside
/// the known set is refused before anything is logged. Real HTTP pipeline, real model validation.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClientErrorLevelIntegrationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClientReports_AreLoggedAtTheirLevel_AndAnUnknownLevelIsRefused()
    {
        var sink = new CapturingLoggerProvider();
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(sink)));
        using var client = host.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, Ct));

        var warning = await Post(client, "warn-1", ClientErrorLogLevels.Warning);
        var legacy = await Post(client, "legacy-1", level: null);
        var error = await Post(client, "error-1", ClientErrorLogLevels.Error);
        var unknown = await Post(client, "verbose-1", "Verbose");
        var wrongCase = await Post(client, "case-1", "warning");

        Assert.Equal(HttpStatusCode.NoContent, warning);
        Assert.Equal(HttpStatusCode.NoContent, legacy);
        Assert.Equal(HttpStatusCode.NoContent, error);
        Assert.Equal(HttpStatusCode.BadRequest, unknown);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCase);
        Assert.Equal(LogLevel.Warning, sink.LevelOf("warn-1"));
        Assert.Equal(LogLevel.Error, sink.LevelOf("legacy-1"));
        Assert.Equal(LogLevel.Error, sink.LevelOf("error-1"));
        Assert.Null(sink.LevelOf("verbose-1"));
        Assert.Null(sink.LevelOf("case-1"));
    }

    private static async Task<HttpStatusCode> Post(HttpClient client, string correlationId, string? level)
    {
        using var response = await client.PostAsJsonAsync("/api/client-errors", new ClientErrorLogRequest
        {
            CorrelationId = correlationId,
            Summary = "Realtime connection restored after an outage",
            Message = "The 'servers' realtime connection was lost and came back after 90 s.",
            Path = "/servers",
            Level = level
        }, Ct);
        return response.StatusCode;
    }

    /// <summary>Keeps the level of every entry the client-errors controller writes, by correlation id.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, LogLevel> _levels = new();

        public LogLevel? LevelOf(string correlationId) =>
            _levels.TryGetValue(correlationId, out var level) ? level : null;

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(ClientErrorsController).FullName ? new Capture(_levels) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private sealed class Capture(ConcurrentDictionary<string, LogLevel> levels) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values
                    && values.FirstOrDefault(pair => pair.Key == "CorrelationId").Value is string correlation)
                    levels[correlation] = logLevel;
            }
        }
    }
}
