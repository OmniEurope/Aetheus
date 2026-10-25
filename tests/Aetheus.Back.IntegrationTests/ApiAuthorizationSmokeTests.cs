// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// HTTP-level authorization sweep across the WHOLE API surface: for one representative GET
/// route per controller, an anonymous (token-less) request must be rejected with 401 by the
/// real JWT middleware before it can ever reach the controller action.
/// <para>
/// This is the runtime complement to <c>ControllerAuthorizationAuditTests</c> (which only
/// asserts the <c>[Authorize]</c> attribute exists statically): it proves the attribute is
/// actually wired into the request pipeline end-to-end for every module. Authentication runs
/// before resource binding, so placeholder ids (e.g. <c>/api/servers/1/...</c>) yield 401
/// regardless of whether the resource exists.
/// </para>
/// </summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class ApiAuthorizationSmokeTests(ApiSmokeFixture fixture)
{
    // One protected GET route per controller. Kept as data so a forgotten [Authorize] on any
    // single module's read path fails this sweep with the offending route in the test name.
    [Theory]
    // --- top-level collection endpoints ---
    [InlineData("/api/projects")]
    [InlineData("/api/servers")]
    [InlineData("/api/users")]
    [InlineData("/api/roles")]
    [InlineData("/api/organizations")]
    [InlineData("/api/agent-pools")]
    [InlineData("/api/audit")]
    [InlineData("/api/dashboards")]
    [InlineData("/api/alerts")]
    [InlineData("/api/monitoring/dashboard")]
    [InlineData("/api/plugins")]
    [InlineData("/api/package-feeds")]
    [InlineData("/api/package-registry")]
    [InlineData("/api/packages/nuget/v3/flatcontainer/sample/index.json")]
    [InlineData("/api/packages/npm/-/ping")]
    [InlineData("/api/packages/maven/org/example/sample/maven-metadata.xml")]
    [InlineData("/api/system-logs/files")]
    [InlineData("/api/releases")]
    [InlineData("/api/pipelines")]
    [InlineData("/api/pipelines/runs/1/checkpoint-resume-preview")]
    [InlineData("/api/pipelines/runs/1/lineage")]
    [InlineData("/api/pipelines/runs/1/migration-gate")]
    [InlineData("/api/pipelines/runs/1/schema-tracking")]
    [InlineData("/api/environments/1/schema-tracking")]
    [InlineData("/api/pipelines/1/runs/1/stage-baselines")]
    // Pipeline approvals have a distinct controller under the pipeline route.
    [InlineData("/api/pipelines/approvals/pending")]
    // Pipeline owner transfer (admin only).
    [InlineData("/api/pipelines/ownership/candidates")]
    // Who reads each library and vault (recette R-65, R-66).
    [InlineData("/api/pipelines/resource-usage")]
    [InlineData("/api/admin/performance")]
    [InlineData("/api/pipelines/templates")]
    [InlineData("/api/work-items")]
    [InlineData("/api/service-connections")]
    [InlineData("/api/test-suites")]
    [InlineData("/api/webhooks")]
    [InlineData("/api/vaults")]
    [InlineData("/api/tasks")]
    [InlineData("/api/variable-libraries")]
    // Nested port allocation has its own controller and needs an independent authorization check.
    [InlineData("/api/variable-libraries/1/ports/servers")]
    [InlineData("/api/settings")]
    [InlineData("/api/settings/daily-recap")]
    [InlineData("/api/settings/daily-recap/preview")]
    [InlineData("/api/mail-templates")]
    [InlineData("/api/mail-templates/NotificationChannelTest/en")]
    [InlineData("/api/environments")]
    [InlineData("/api/git/repos")]
    [InlineData("/api/git/connections")]
    [InlineData("/api/notifications/channels")]
    [InlineData("/api/notifications/me/unread-count")]
    [InlineData("/api/users/me/preferences")]
    [InlineData("/api/auth/registration-tokens")]
    [InlineData("/api/agent/installer/linux")]
    [InlineData("/api/personal-access-tokens")]
    [InlineData("/api/backups")]
    [InlineData("/api/analysis/policies/global")]
    [InlineData("/api/ai/profiles")]
    // AppMonitoringController (api/[controller]) and AppTelemetryController (api/appmonitoring) share the
    // normalized prefix api/appmonitoring, so ONE literal satisfies the prefix-based coverage guard for
    // both - but the anonymous-401 assertion only fires on the route it actually hits. Exercise a route
    // owned by EACH controller so both [Authorize] wirings are proven end-to-end (auth runs before model
    // binding, so the placeholder id yields 401 regardless of whether the app exists).
    [InlineData("/api/appmonitoring/summary")]
    [InlineData("/api/appmonitoring/apps/1/metrics/names")]
    [InlineData("/api/appmonitoring/apps/1/send-interval")]
    [InlineData("/metrics")]
    // Covers retention, storage and reclamation authorization; ingestion configuration requires its
    // ingest key.
    [InlineData("/api/projects/1/data-retention")]
    [InlineData("/api/projects/1/release-series")]
    [InlineData("/api/data-storage/projects")]
    [InlineData("/api/data-storage/reclaim")]
    [InlineData("/api/ingest/otlp/v1/configuration")]
    // --- project-scoped endpoints ---
    [InlineData("/api/artifacts/project/1")]
    [InlineData("/api/external-repos/project/1")]
    [InlineData("/api/gitgraph/project/1")]
    [InlineData("/api/logs/task/1")]
    // --- server-scoped endpoints ---
    [InlineData("/api/servers/1/certbot")]
    [InlineData("/api/servers/1/docker/containers")]
    [InlineData("/api/servers/1/apache")]
    [InlineData("/api/servers/1/mail")]
    [InlineData("/api/servers/1/mail/client-settings")]
    [InlineData("/api/servers/1/mail/accounts/1/mailbox")]
    [InlineData("/api/servers/1/mail/accounts/1/mailbox/messages/1")]
    [InlineData("/api/mail/mailboxes")]
    [InlineData("/api/servers/1/module-links")]
    [InlineData("/api/servers/1/portsentry")]
    [InlineData("/api/servers/1/rkhunter")]
    [InlineData("/api/servers/1/teamspeak")]
    [InlineData("/api/servers/1/apps")]
    [InlineData("/api/servers/1/modules")]
    [InlineData("/api/servers/1/configuration/export")]
    [InlineData("/api/servers/1/ports")]
    public async Task ProtectedGetEndpoint_Anonymous_Returns401(string route)
    {
        using var client = fixture.CreateAnonymousClient();

        using var response = await client.GetAsync(route, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Mutations are the most dangerous paths, so the anonymous-rejection guarantee must hold for
    // them too - not just GETs. Authentication runs before model binding, so a placeholder body /
    // id yields 401 at the middleware regardless of the payload or whether the resource exists.
    [Theory]
    [InlineData("POST", "/api/projects")]
    [InlineData("POST", "/api/agent-pools")]
    [InlineData("POST", "/api/users")]
    [InlineData("POST", "/api/organizations")]
    [InlineData("POST", "/api/vaults")]
    [InlineData("POST", "/api/environments")]
    [InlineData("POST", "/api/servers/1/docker/action")]
    // The mutation-only port-observation controller needs its own anonymous-write rejection case.
    [InlineData("POST", "/api/servers/1/ports/observe")]
    // Recette R3-59: the read-only package list is a POST (it queues an agent task); anonymous stays out.
    [InlineData("POST", "/api/servers/1/packages/inventory")]
    // CronController is mutation-only (no GET) - without these it would be invisible to the GET
    // coverage guard AND absent from any anonymous-rejection sweep.
    [InlineData("POST", "/api/servers/1/cron")]
    [InlineData("DELETE", "/api/servers/1/cron")]
    // ClientErrorsController is mutation-only too, and its single POST writes attacker-controlled text
    // straight into the server log - an anonymous caller must never reach it.
    [InlineData("POST", "/api/pipelines/setup/readiness")]
    [InlineData("POST", "/api/client-errors")]
    [InlineData("PUT", "/api/users/1")]
    [InlineData("PUT", "/api/users/me/preferences/aetheus.appearance")]
    [InlineData("DELETE", "/api/users/me/preferences/aetheus.appearance")]
    [InlineData("PUT", "/api/agent-pools/1")]
    [InlineData("DELETE", "/api/projects/1")]
    [InlineData("DELETE", "/api/agent-pools/1")]
    [InlineData("DELETE", "/api/vaults/1")]
    [InlineData("PUT", "/api/projects/1/data-retention")]
    [InlineData("POST", "/api/data-storage/reclaim")]
    [InlineData("POST", "/api/mail/no-reply-sender")]
    [InlineData("PUT", "/api/settings/daily-recap")]
    [InlineData("POST", "/api/settings/daily-recap/send-now")]
    [InlineData("POST", "/api/ai/profiles/1/model-list/refresh")]
    [InlineData("POST", "/api/servers/1/mail/accounts/1/mailbox/unlock")]
    [InlineData("POST", "/api/servers/1/mail/accounts/1/mailbox/send")]
    [InlineData("PUT", "/api/servers/1/mail/accounts/1/mailbox/messages/1/read")]
    [InlineData("DELETE", "/api/servers/1/mail/accounts/1/mailbox/messages/1")]
    public async Task ProtectedMutationEndpoint_Anonymous_Returns401(string method, string route)
    {
        using var client = fixture.CreateAnonymousClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT")
            request.Content = JsonContent.Create(new { });

        using var response = await client.SendAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
