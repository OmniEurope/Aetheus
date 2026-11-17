// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-1, the guard that holds the system logs at zero (point 4 of the row): the application's own healthy journeys, driven
/// through the real HTTP pipeline against PostgreSQL, write no line at Warning, Error or Critical. Anything one of them
/// raises is a defect to fix at its cause, or a legitimate outcome that is not a healthy journey and belongs in a test that
/// provokes it on purpose.
/// </summary>
// Serial, on its own smoke host: the host's slow-connection probe and slow-request diagnostics read
// process-wide meters and timings, so a host of any class running beside it writes Warning lines here.
[Collection(SerialPostgresCollection.Name)]
public sealed class SystemLogsStayAtZeroIntegrationTests(ApiSmokeFixture fixture) : IClassFixture<ApiSmokeFixture>
{
    /// <summary>Without this the guard below could be green because it sees nothing: a request the application refuses on
    /// a real route is a Warning line, and the recorder must have it.</summary>
    [Fact]
    public async Task TheGuard_SeesTheWarningOfARefusedRequest()
    {
        var mark = fixture.Factory.Warnings.Mark();
        using var client = fixture.CreateAdminClient();

        using var response = await client.PostAsync(
            "/api/projects", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(fixture.Factory.Warnings.Since(mark), line => line.Contains("HTTP 400 for POST /api/projects", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAdministratorReadingTheApplication_WritesNothingAtWarningOrAbove()
    {
        string[] routes =
        [
            "/api/projects", "/api/servers", "/api/servers/names", "/api/users/me", "/api/organizations/me",
            "/api/pipelines", "/api/pipelines/templates", "/api/dashboards", "/api/alerts", "/api/monitoring/dashboard",
            "/api/system-logs/files", "/api/releases", "/api/tasks", "/api/tasks/active", "/api/settings",
            "/api/analysis/cve-watch?organizationId=1", "/api/appmonitoring/summary", "/api/notifications/me/unread-count"
        ];
        var mark = fixture.Factory.Warnings.Mark();
        using var client = fixture.CreateAdminClient();

        foreach (var route in routes)
        {
            using var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // A background loop that fails because of what the journey did writes its line a moment later.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var written = fixture.Factory.Warnings.Since(mark);
        Assert.True(written.Count == 0, "The journey wrote Warning-or-above lines:" + Environment.NewLine + string.Join(Environment.NewLine, written));
    }
}
