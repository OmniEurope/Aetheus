// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aetheus.WebAnalytics.Tests;

public sealed class AetheusWebAnalyticsEndpointTests
{
    [Fact]
    public async Task Collect_EnforcesPrivacyValidationAndRequestSize()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var gpcRequest = EventRequest(ValidEvent());
        gpcRequest.Headers.Add("Sec-GPC", "1");
        using var gpcResponse = await client.SendAsync(
            gpcRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, gpcResponse.StatusCode);

        using var invalidSchemaResponse = await client.PostAsJsonAsync(
            "/aetheus-analytics/v1/events",
            ValidEvent() with { SchemaVersion = 2 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidSchemaResponse.StatusCode);

        using var unsupportedResponse = await client.PostAsJsonAsync(
            "/aetheus-analytics/v1/events",
            ValidEvent() with { Kind = "session_start" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupportedResponse.StatusCode);

        using var unsafeRouteResponse = await client.PostAsJsonAsync(
            "/aetheus-analytics/v1/events",
            ValidEvent() with { Route = "/projects/42?secret=value" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsafeRouteResponse.StatusCode);

        using var acceptedResponse = await client.PostAsJsonAsync(
            "/aetheus-analytics/v1/events",
            ValidEvent(),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, acceptedResponse.StatusCode);

        using var oversizedResponse = await client.PostAsync(
            "/aetheus-analytics/v1/events",
            new StringContent(
                "{\"padding\":\"" + new string('a', 5000) + "\"}",
                System.Text.Encoding.UTF8,
                "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);
    }

    [Fact]
    public async Task Collect_ReturnsTooManyRequestsAfterAddressBudget()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        for (var index = 0; index < 60; index++)
        {
            using var response = await client.PostAsJsonAsync(
                "/aetheus-analytics/v1/events",
                ValidEvent() with { EventId = Guid.NewGuid() },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        using var rejected = await client.PostAsJsonAsync(
            "/aetheus-analytics/v1/events",
            ValidEvent() with { EventId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Collect_AcceptsHeartbeatAndExportsItToTheIngestEndpoint()
    {
        var exported = new AnalyticsExportRecordingHandler();
        await using var app = await StartAsync(exportHandler: exported);
        using var client = app.GetTestClient();
        var heartbeat = ValidEvent() with { Kind = "heartbeat" };

        using var response = await client.PostAsJsonAsync(
            "/aetheus-analytics/v1/events",
            heartbeat,
            TestContext.Current.CancellationToken);
        var received = await exported.Received.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(heartbeat.EventId.ToString(), received.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"kind\":\"heartbeat\"", received.Body, StringComparison.OrdinalIgnoreCase);

        using var measuredHeartbeat = await client.PostAsJsonAsync(
            "/aetheus-analytics/v1/events",
            heartbeat with { EventId = Guid.NewGuid(), DurationMs = 12 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, measuredHeartbeat.StatusCode);
    }

    [Fact]
    public async Task Collect_AcceptsEveryEventTheBrowserModuleEmits()
    {
        // The browser module and this collector evolve in separate files: when the module learned the
        // 75 s heartbeat (e5e96972a) the collector kept refusing that kind, so every heartbeat became a
        // 422 in the visitor's console until 489db9b5a. The emitted kinds are read from the module
        // itself so the next kind the module learns fails here instead of in production.
        var emitted = BrowserModuleEvents();
        Assert.Contains(emitted, analyticsEvent => analyticsEvent.Kind == "page_view");
        Assert.Contains(emitted, analyticsEvent => analyticsEvent.Kind == "heartbeat");
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        // "/" is the dashboard; the bootstrap resolver replaces every non-static nested segment.
        foreach (var route in new[] { "/", "/pipelines/{value}/runs/{value}" })
        {
            foreach (var (kind, details) in emitted)
            {
                var payload = new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["eventId"] = Guid.NewGuid().ToString(),
                    ["occurredAtUtc"] = DateTime.UtcNow.ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                        CultureInfo.InvariantCulture),
                    ["kind"] = kind,
                    ["route"] = route,
                    ["signedIn"] = true
                };
                foreach (var (field, value) in details)
                    payload[field] = value.DeepClone();

                using var response = await client.PostAsync(
                    "/aetheus-analytics/v1/events",
                    new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                    TestContext.Current.CancellationToken);
                Assert.True(
                    response.StatusCode == HttpStatusCode.Accepted,
                    $"{payload.ToJsonString()} -> {(int)response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task PreferenceMutations_RequireSameOriginAndAuthenticatedWriter()
    {
        await using var app = await StartAsync(authenticated: true);
        using var client = app.GetTestClient();

        using var crossOriginRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/aetheus-analytics/v1/opt-out");
        crossOriginRequest.Headers.Add("Origin", "https://attacker.invalid");
        using var crossOriginResponse = await client.SendAsync(
            crossOriginRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossOriginResponse.StatusCode);

        using var sameOriginRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/aetheus-analytics/v1/opt-out");
        sameOriginRequest.Headers.Add("Origin", "http://localhost");
        using var sameOriginResponse = await client.SendAsync(
            sameOriginRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotImplemented, sameOriginResponse.StatusCode);
    }

    private static async Task<WebApplication> StartAsync(
        bool authenticated = false,
        HttpMessageHandler? exportHandler = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAetheusWebAnalytics(
            new ConfigurationBuilder().Build(),
            options =>
            {
                options.Enabled = true;
                options.ApplicationId = 42;
                options.SiteId = "portfolio";
                options.IngestEndpoint = new Uri(
                    "https://aetheus.example/api/ingest/web-analytics/v1/events");
                options.IngestKey = "ingest";
                options.PseudonymizationKey = new string('0', 32);
                options.ExportIntervalMilliseconds = exportHandler is null ? 10_000 : 100;
            });
        if (exportHandler is not null)
        {
            builder.Services
                .AddHttpClient(AnalyticsExportService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => exportHandler);
        }

        var app = builder.Build();
        if (authenticated)
        {
            app.Use(async (context, next) =>
            {
                context.User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "user-42")],
                        "test"));
                await next(context);
            });
        }
        app.MapAetheusWebAnalytics();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpRequestMessage EventRequest(AnalyticsBrowserEvent analyticsEvent) =>
        new(HttpMethod.Post, "/aetheus-analytics/v1/events")
        {
            Content = JsonContent.Create(analyticsEvent)
        };

    /// <summary>
    /// Every <c>emit("kind", { field: value })</c> call of the shipped browser module, with a literal
    /// value kept as is and the measured <c>durationMs</c> replaced by a sample the module can produce.
    /// </summary>
    private static List<(string Kind, Dictionary<string, JsonNode> Details)> BrowserModuleEvents()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aetheus.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(
            directory.FullName, "packages", "aetheus-web-analytics", "src", "aetheus-web-analytics.js"));

        var events = new List<(string Kind, Dictionary<string, JsonNode> Details)>();
        foreach (Match call in Regex.Matches(
                     source,
                     "emit\\(\"(?<kind>[a-z_]+)\"(?:,\\s*\\{\\s*(?<field>[A-Za-z]+):\\s*(?<value>[^}]*?)\\s*\\})?\\)"))
        {
            var details = new Dictionary<string, JsonNode>();
            if (call.Groups["field"].Success)
            {
                var field = call.Groups["field"].Value;
                var value = call.Groups["value"].Value;
                details[field] = field switch
                {
                    "errorType" when value.StartsWith('"') => JsonValue.Create(value.Trim('"')),
                    "durationMs" => JsonValue.Create(1234),
                    _ => throw new InvalidOperationException(
                        $"The module emits an unknown detail '{field}: {value}'; teach this test its shape.")
                };
            }
            events.Add((call.Groups["kind"].Value, details));
        }

        // A call the pattern above cannot read (a computed kind, two detail fields) would otherwise be
        // skipped in silence and its event never sent to the collector by this test.
        var callSites = Regex.Count(source, "(?<!function )\\bemit\\(");
        Assert.True(callSites == events.Count,
            $"The module has {callSites} emit(...) calls but this test read {events.Count}; teach it the new shape.");
        return events;
    }

    private static AnalyticsBrowserEvent ValidEvent() => new()
    {
        SchemaVersion = 1,
        EventId = Guid.NewGuid(),
        OccurredAtUtc = DateTimeOffset.UtcNow,
        Kind = "page_view",
        Route = "/projects/42"
    };
}
