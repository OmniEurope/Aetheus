// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The frontend contract of the smoke step: the origin's root must answer 2xx with an HTML shell marked
/// <c>no-cache</c> (revalidated, never <c>no-store</c>: recette R-8), and each way of missing it is one blocking finding with its own explanation. Readiness
/// answers 200 throughout, so the frontend is the only probe that can find anything. The probes go through
/// the handler the executor is given, never a listening socket.
/// </summary>
public sealed class SmokeFrontendContractTests
{
    private const string Origin = "http://127.0.0.1:19999";

    public static TheoryData<string, string> Shells => new()
    {
        { "error", "Frontend returned 502." },
        { "not-html", "Frontend did not return an HTML shell." },
        { "cacheable", "Frontend shell is not revalidated on every use; a cached shell strands the app on retired runtime hashes." },
        { "no-store", "Frontend shell is not revalidated on every use; a cached shell strands the app on retired runtime hashes." },
        { "private","Frontend shell is not revalidated on every use; a cached shell strands the app on retired runtime hashes." },
        { "no-header", "Frontend shell is not revalidated on every use; a cached shell strands the app on retired runtime hashes." },
        { "unreachable", "Frontend probe failed: connection refused" },
    };

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task AFrontendThatBreaksTheContract_IsOneBlockingFinding(string shell, string expected)
    {
        var (result, log) = await Smoke(shell);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains((expected, TaskLogLevel.Warning), log);
        Assert.Contains(("##aetheus[setvariable name=SMOKE_BLOCKING_FINDINGS]1", TaskLogLevel.Info), log);
        Assert.DoesNotContain(log, line => line.Message.StartsWith("Frontend contract passed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANoCacheHtmlShell_PassesTheContract()
    {
        var (result, log) = await Smoke("no-cache");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(("Frontend contract passed (HTML shell, no-cache).", TaskLogLevel.Info), log);
        Assert.Contains(("##aetheus[setvariable name=SMOKE_FINDINGS]0", TaskLogLevel.Info), log);
    }

    private static async Task<(ExecutorResult Result, List<(string Message, TaskLogLevel Level)> Log)> Smoke(string shell)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new OriginHandler(shell)));
        var executor = new SmokeOperationExecutor(
            Substitute.For<IShellRunner>(), Options.Create(new AetheusAgentOptions()), factory,
            NullLogger<SmokeOperationExecutor>.Instance);
        var log = new List<(string, TaskLogLevel)>();

        var result = await executor.ExecuteAsync(
            OperationKind.PipelineSmoke, Origin, new Dictionary<string, string> { ["AETHEUS_SMOKE_FRONTEND"] = "true" }, 60,
            (message, level) => { log.Add((message, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
        return (result, log);
    }

    // Readiness always answers 200; the root answers according to the shell under test.
    private sealed class OriginHandler(string shell) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/health/ready")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            Assert.Equal(Origin + "/", request.RequestUri.AbsoluteUri);
            if (shell == "unreachable")
                throw new HttpRequestException("connection refused");
            var response = new HttpResponseMessage(shell == "error" ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new StringContent(shell == "not-html" ? "{\"status\":\"ok\"}" : "<!doctype html><html><body></body></html>"),
            };
            response.Headers.CacheControl = shell switch
            {
                "no-store" => new CacheControlHeaderValue { NoStore = true, NoCache = true, MustRevalidate = true },
                "no-cache" => new CacheControlHeaderValue { NoCache = true },
                "private" => new CacheControlHeaderValue { Private = true },
                "no-header" => null,
                _ => new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) },
            };
            return Task.FromResult(response);
        }
    }
}
