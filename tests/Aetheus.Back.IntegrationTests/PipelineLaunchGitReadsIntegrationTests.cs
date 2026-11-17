// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Aetheus.Back.Components.Git;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-2: <c>GET /api/pipelines/{id}/parameters</c>, <c>POST .../preflight</c> and <c>POST .../run</c> took about 2 s for
/// 14 to 300 ms of database time. The remaining cost is the pipeline definition read from the project's internal Git
/// repository. This runs the three requests against a real bare repository on disk and PostgreSQL, through the real
/// Git CLI service wrapped in a recorder, and reads what each request asked Git for: how many calls, of which kind,
/// in how many milliseconds. The default branch of a repository is detected (two git processes, and a possible write)
/// only by the calls that must know the branch on disk; a read of a definition names the stored default branch and
/// detects nothing.
/// </summary>
public sealed class PipelineLaunchGitReadsIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadingThePipelineDefinitionForALaunch_DetectsNoDefaultBranch()
    {
        var recorder = new GitCallRecorder();
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IGitLightCliService>(provider => GitCallRecorder.Wrap(
                ActivatorUtilities.CreateInstance<GitLightCliService>(provider), recorder))));
        using var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, Ct));
        var pipelineId = (await PipelineLaunchScenario.SeedAsync(factory, client, followUp: false)).PipelineId;
        var request = new PipelineRunRequest { Parameters = new Dictionary<string, string> { ["target"] = "staging" } };

        recorder.Reset();
        var parametersTime = await TimeAsync(() => client.GetAsync($"/api/pipelines/{pipelineId}/parameters", Ct));
        var parameters = recorder.Take();
        var preflightTime = await TimeAsync(() => client.PostAsJsonAsync($"/api/pipelines/{pipelineId}/preflight", request, IntegrationJsonOptions.Default, Ct));
        var preflight = recorder.Take();
        var runTime = await TimeAsync(() => client.PostAsJsonAsync($"/api/pipelines/{pipelineId}/run", request, IntegrationJsonOptions.Default, Ct));
        var run = recorder.Take();

        var warmParametersTime = await TimeAsync(() => client.GetAsync($"/api/pipelines/{pipelineId}/parameters", Ct));
        var warmParameters = recorder.Take();
        var warmPreflightTime = await TimeAsync(() => client.PostAsJsonAsync($"/api/pipelines/{pipelineId}/preflight", request, IntegrationJsonOptions.Default, Ct));
        var warmPreflight = recorder.Take();

        Console.WriteLine($"[R-2 git reads] warm GET parameters {warmParametersTime.Elapsed.TotalMilliseconds:F0} ms: {Describe(warmParameters)}");
        Console.WriteLine($"[R-2 git reads] warm POST preflight {warmPreflightTime.Elapsed.TotalMilliseconds:F0} ms: {Describe(warmPreflight)}");
        Console.WriteLine($"[R-2 git reads] GET parameters {parametersTime.Elapsed.TotalMilliseconds:F0} ms: {Describe(parameters)}");
        Console.WriteLine($"[R-2 git reads] POST preflight {preflightTime.Elapsed.TotalMilliseconds:F0} ms: {Describe(preflight)}");
        Console.WriteLine($"[R-2 git reads] POST run {runTime.Elapsed.TotalMilliseconds:F0} ms: {Describe(run)}");
        Assert.Equal(HttpStatusCode.OK, parametersTime.Status);
        Assert.Equal(HttpStatusCode.OK, preflightTime.Status);
        Assert.Equal(HttpStatusCode.Created, runTime.Status);
        Assert.NotEmpty(parameters);
        Assert.DoesNotContain(parameters.Concat(preflight).Concat(run), call => call.Method == nameof(IGitLightCliService.DetectDefaultBranchAsync));
    }

    private static string Describe(IReadOnlyList<GitCallRecorder.Call> calls) =>
        calls.Count == 0
            ? "no git call"
            : string.Join(", ", calls.GroupBy(call => call.Method).Select(group =>
                $"{group.Key} x{group.Count()} ({group.Sum(call => call.Milliseconds):F0} ms) [{string.Join(" | ", group.Select(call => call.Target))}]"));

    private static async Task<(HttpStatusCode Status, TimeSpan Elapsed)> TimeAsync(Func<Task<HttpResponseMessage>> send)
    {
        var watch = Stopwatch.StartNew();
        using var response = await send();
        return (response.StatusCode, watch.Elapsed);
    }

    /// <summary>What the Git CLI service was asked, in order, with how long each call took.</summary>
    public sealed class GitCallRecorder
    {
        /// <summary>One call, counted when it starts; its duration is filled in when its task ends (informational).</summary>
        public sealed class Call(string method, string target)
        {
            public string Method { get; } = method;

            /// <summary>The revision and path the call named (the repository's disk path left out).</summary>
            public string Target { get; } = target;

            public double Milliseconds { get; set; }
        }

        private readonly object _gate = new();
        private readonly List<Call> _calls = [];

        public static IGitLightCliService Wrap(IGitLightCliService inner, GitCallRecorder sink)
        {
            var proxy = DispatchProxy.Create<IGitLightCliService, RecordingProxy>();
            var recording = (RecordingProxy)(object)proxy;
            recording.Inner = inner;
            recording.Sink = sink;
            return proxy;
        }

        public void Reset()
        {
            lock (_gate) _calls.Clear();
        }

        public IReadOnlyList<Call> Take()
        {
            lock (_gate)
            {
                var taken = _calls.ToList();
                _calls.Clear();
                return taken;
            }
        }

        internal void Add(Call call)
        {
            lock (_gate) _calls.Add(call);
        }
    }

    /// <summary>Forwards every call of the Git CLI service to the real one and records its name and duration; it
    /// changes nothing the service does.</summary>
    public class RecordingProxy : DispatchProxy
    {
        internal IGitLightCliService Inner { get; set; } = null!;

        internal GitCallRecorder Sink { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var call = new GitCallRecorder.Call(targetMethod!.Name, string.Join(" ", (args ?? []).OfType<string>().Skip(1)));
            Sink.Add(call);
            var watch = Stopwatch.StartNew();
            object? result;
            try
            {
                result = targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }

            if (result is Task task)
                _ = task.ContinueWith(_ => call.Milliseconds = watch.Elapsed.TotalMilliseconds, TaskScheduler.Default);
            else
                call.Milliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}
