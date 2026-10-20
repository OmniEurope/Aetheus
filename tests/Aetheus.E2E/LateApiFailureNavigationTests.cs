// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>A failing API call belongs to the page that made it: once the user has moved on, its
/// late failure must not raise the application error card (or the Blazor fatal banner) on another
/// page, and an error card must never follow the user to the next page.</summary>
[Category("E2E")]
[Category("Pipelines")]
[Category("Logs")]
public sealed class LateApiFailureNavigationTests : E2ETestBase
{
    protected override bool ShareBrowserContextAcrossTests => true;

    // The route order of the responsive sweep in which the defect was first seen.
    private static readonly string[] PathsAfterPipelines =
    [
        "/releases",
        "/environments",
        "/tasks",
        "/alerts",
        "/backups",
        "/vaults",
        "/variable-libraries",
        "/service-connections",
        "/git-repositories",
        "/logs",
        "/settings"
    ];

    // The API client retries a 5xx once (FrontendRuntimeDefaults.ApiMaximumRetryAttempts), and the fleet
    // loads pass no cancellation token, so every fleet load the pages start sends exactly two attempts.
    private const int AttemptsPerFleetLoad = 2;
    private const string ForcedFailureMessage = "forced fleet failure";

    private int _fleetAttempts;
    private int _fleetFailureReports;

    [SetUp]
    public async Task SetUp()
    {
        await LoginAsync();
        _fleetAttempts = 0;
        _fleetFailureReports = 0;
        // ErrorNotificationHandler reports the final failure of a load (after its retry) to the system log.
        Page.Request += (_, request) =>
        {
            if (request.Method == "POST"
                && new Uri(request.Url).AbsolutePath.EndsWith("/api/client-errors", StringComparison.Ordinal)
                && request.PostData?.Contains(ForcedFailureMessage, StringComparison.Ordinal) == true)
                Interlocked.Increment(ref _fleetFailureReports);
        };
        AllowBrowserDiagnostic(new Regex("Failed to load resource.*500", RegexOptions.IgnoreCase));
        AllowBrowserDiagnostic(new Regex("Polly.*Result: '500'", RegexOptions.IgnoreCase | RegexOptions.Singleline));
        // Every fleet page request fails on the server, the filter-values endpoint stays real.
        await Page.RouteAsync(
            url => new Uri(url).AbsolutePath.EndsWith("/api/pipelines/fleet", StringComparison.Ordinal),
            route =>
            {
                Interlocked.Increment(ref _fleetAttempts);
                return route.FulfillAsync(new()
                {
                    Status = 500,
                    ContentType = "application/json",
                    Body = $"{{\"message\":\"{ForcedFailureMessage}\"}}"
                });
            });
    }

    [Test]
    public async Task FleetFailure_ThenSweepThroughOtherPages_NeverRaisesTheErrorCardElsewhere()
    {
        await NavigateToAsync("/pipelines");
        await Expect(Page.Locator(".omni-data-grid").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        await WaitForNoSpinnerAsync();
        await AssertNoErrorSurfaceAsync("/pipelines");

        foreach (var path in PathsAfterPipelines)
        {
            await NavigateInsideTheApplicationAsync(path);
            await WaitForNoSpinnerAsync();
            await AssertNoErrorSurfaceAsync(path);
        }

        // A late failure is late: wait until every fleet load has failed for good.
        await WaitForEveryFleetLoadToFailForGoodAsync();
        await AssertNoErrorSurfaceAsync("/settings (after waiting)");
    }

    [Test]
    public async Task FleetFailure_LeavingPipelinesWhileItsLoadIsPending_NeverRaisesTheErrorCardElsewhere()
    {
        await NavigateToAsync("/pipelines");
        // Leave at once: the fleet request and its retry are still in flight.
        await NavigateInsideTheApplicationAsync("/logs");
        await WaitForEveryFleetLoadToFailForGoodAsync();
        await AssertNoErrorSurfaceAsync("/logs");

        await NavigateInsideTheApplicationAsync("/settings");
        await WaitForNoSpinnerAsync();
        await WaitForEveryFleetLoadToFailForGoodAsync();
        await AssertNoErrorSurfaceAsync("/settings");
    }

    [Test]
    public async Task FleetFailure_HoppingThroughPagesBeforeTheyFinishLoading_NeverRaisesTheErrorCardElsewhere()
    {
        await NavigateToAsync("/pipelines");
        // Each page is left as soon as the router has rendered it, while its own loads are in flight.
        foreach (var path in PathsAfterPipelines.TakeWhile(path => path != "/logs"))
            await NavigateInsideTheApplicationAsync(path);
        await NavigateInsideTheApplicationAsync("/logs");
        await WaitForEveryFleetLoadToFailForGoodAsync();
        await AssertNoErrorSurfaceAsync("/logs (after hopping)");

        await NavigateInsideTheApplicationAsync("/settings");
        await WaitForNoSpinnerAsync();
        await AssertNoErrorSurfaceAsync("/settings (after hopping)");
    }

    /// <summary>
    /// Waits for the late failures to have landed instead of a fixed delay: every fleet load sent both
    /// its attempts and its final failure went through ErrorNotificationHandler, which reports it to
    /// /api/client-errors. The pages then get two animation frames and their loaders to render what that
    /// failure caused, which the assertion that follows reads.
    /// </summary>
    private async Task WaitForEveryFleetLoadToFailForGoodAsync()
    {
        var waited = Stopwatch.StartNew();
        while (!EveryFleetLoadFailedForGood(out var attempts, out var reports))
        {
            if (waited.ElapsedMilliseconds > PlaywrightConfig.AppReadyTimeoutMs)
            {
                Assert.Fail(
                    $"The forced fleet failures did not all land within {PlaywrightConfig.AppReadyTimeoutMs} ms: "
                    + $"{attempts} fleet attempt(s) (expected a non-zero multiple of {AttemptsPerFleetLoad}), "
                    + $"{reports} client-error report(s) of the final failure (expected {attempts / AttemptsPerFleetLoad}).");
            }
            await Task.Delay(100);
        }
        await Page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        await WaitForNoSpinnerAsync();
    }

    private bool EveryFleetLoadFailedForGood(out int attempts, out int reports)
    {
        attempts = Volatile.Read(ref _fleetAttempts);
        reports = Volatile.Read(ref _fleetFailureReports);
        return attempts > 0
            && attempts % AttemptsPerFleetLoad == 0
            && reports >= attempts / AttemptsPerFleetLoad;
    }

    private async Task AssertNoErrorSurfaceAsync(string path)
    {
        var errorCards = await Page.Locator(".error-card").AllInnerTextsAsync();
        var fatalBannerVisible = await Page.EvaluateAsync<bool>("""
            () => {
                const banner = document.getElementById('blazor-error-ui');
                return !!banner && getComputedStyle(banner).display !== 'none';
            }
            """);
        Assert.Multiple(() =>
        {
            Assert.That(errorCards, Is.Empty,
                $"{path} must not show the application error card. Card: {string.Join(" | ", errorCards)}");
            Assert.That(fatalBannerVisible, Is.False,
                $"{path} must not show the Blazor unhandled-error banner.");
        });
    }
}
