// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aetheus.E2E;

/// <summary>
/// A360-43. The sixty existing E2E tests exercise the interface; not one of them ever LAUNCHED a
/// pipeline, so the whole launch path - preparation, parameter resolution, variable resolution,
/// preflight, refusal or persistence - had no end-to-end coverage at all. Every test of it ran against
/// mocks.
///
/// Scope, stated plainly: this does not run a pipeline to completion. That needs an enrolled agent
/// online, and the E2E harness starts a backend, a frontend and a dedicated database - no agent. What
/// it does cover end to end, through the real HTTP stack and the real database, is everything up to
/// the point where an agent would be needed: a pipeline whose stage names a runner nobody is
/// configured for must be REFUSED at launch, with a readable reason, instead of being accepted and
/// dying at its first stage.
///
/// That refusal is the behaviour added this week (the launch-time preflight), and it was the one part
/// of it no test could observe for real.
/// </summary>
[Category("E2E")]
[Category("Pipelines")]
[RollbackRelevant]
public class PipelineLaunchPathTests : E2ETestBase
{
    [SetUp]
    public async Task SetUp() => await LoginAsync();

    [Test]
    public async Task LaunchingAPipelineNoRunnerCanServe_IsRefusedAtLaunchWithAReadableReason()
    {
        using var http = AuthenticatedClient();
        var projectId = await FirstProjectIdAsync(http);

        // A pure orchestration pipeline: every step is `type: trigger`, so no workspace is required and
        // the launch is not stopped earlier by the "no canonical source repository" check - which is
        // what a `shell` step hits on a project with no repository, as the first run of this test showed.
        //
        // Two unmet requirements at once, both of them unmatchable by construction rather than merely
        // unavailable: a stage pinned to an agent nobody is enrolled as, and a trigger naming a pipeline
        // that does not exist. A runner that is simply OFFLINE must not be refused - that is the
        // scheduler's stand-by path - which is why the selector names an agent that cannot exist.
        var yaml = """
            name: e2e-unmatchable
            stages:
              - name: Orchestrate
                agent: e2e-agent-that-cannot-exist
                steps:
                  - name: call-missing
                    type: trigger
                    pipeline: e2e-pipeline-that-does-not-exist
            """;

        using var created = await http.PostAsJsonAsync(
            $"{BackendUrl}/api/pipelines",
            new { name = $"e2e-launch-{Guid.NewGuid():N}"[..24], projectId, yamlDefinition = yaml });
        created.EnsureSuccessStatusCode();
        var pipeline = await created.Content.ReadFromJsonAsync<JsonElement>();
        var pipelineId = pipeline.GetProperty("id").GetInt32();

        using var launch = await http.PostAsJsonAsync(
            $"{BackendUrl}/api/pipelines/{pipelineId}/run", new { });

        // Refused, not accepted-then-failed: no run row should exist for a requirement that cannot be met.
        Assert.That(
            (int)launch.StatusCode,
            Is.EqualTo(400),
            "A stage whose selector no server can satisfy must be refused at launch.");

        var body = await launch.Content.ReadAsStringAsync();
        Assert.That(
            body,
            Does.Contain("e2e-pipeline-that-does-not-exist"),
            "The refusal must name the missing pipeline, or it is not actionable. This is the launch-time "
            + $"preflight answering; the whole response was: {body}");
        Assert.That(
            body,
            Does.Contain("e2e-agent-that-cannot-exist"),
            "The preflight collects EVERY unmet requirement rather than stopping at the first, so the "
            + $"unmatchable selector must be named too. The whole response was: {body}");

        var runs = await http.GetFromJsonAsync<JsonElement>(
            $"{BackendUrl}/api/pipelines/{pipelineId}/runs?page=1&pageSize=5");
        Assert.That(
            runs.GetProperty("totalCount").GetInt32(),
            Is.Zero,
            "The refusal happens before the run row exists; a persisted run would mean the launch was "
            + "accepted and then died, which is the behaviour the preflight replaced.");
    }

    private static HttpClient AuthenticatedClient()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        using var storageState = JsonDocument.Parse(E2EAuthSession.StorageStateJson!);
        var token = storageState.RootElement.GetProperty("origins")[0]
            .GetProperty("localStorage")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "aetheus_auth_token")
            .GetProperty("value")
            .GetString();
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<int> FirstProjectIdAsync(HttpClient http)
    {
        var projects = await http.GetFromJsonAsync<JsonElement>(
            $"{BackendUrl}/api/projects?page=1&pageSize=1");
        var items = projects.GetProperty("items");
        if (items.GetArrayLength() == 0)
            throw new InvalidOperationException("The E2E database has no project to attach a pipeline to.");
        return items[0].GetProperty("id").GetInt32();
    }
}
