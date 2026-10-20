// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

public sealed class AppIngestionSettingsBehaviorTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public AppIngestionSettingsBehaviorTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse(
            "web-analytics/configuration",
            new AppWebAnalyticsConfigurationDto
            {
                SiteId = "test-site",
                AllowedOrigins = ["https://example.test"],
                StorageBudgetBytes = 104_857_600
            });
        // The storage line under the budget field reads the audience summary.
        _handler.SetJsonResponse("/web-analytics?days=30", new AppWebAnalyticsSummaryDto());
    }

    [Fact]
    public void ReadOnlyApp_LoadsAndRendersThresholdsWithoutMutationControls()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>
        {
            new() { Id = 3, MonitoredAppId = 7, MetricName = "cpu", Operator = ComparisonOperator.GreaterThan, Threshold = 90, IsBreached = true }
        });

        var cut = Render<AppIngestionSettings>(p => p
            .Add(x => x.AppId, 7)
            .Add(x => x.App, new MonitoredAppDto { Id = 7, Name = "API", HasIngestKey = true, IngestDroppedCount = 2 })
            .Add(x => x.CanWrite, false));

        Assert.Contains("cpu", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Breached", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("IngestDroppedCount", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("GenerateKey", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R2007_TheAudienceStorage_IsOneLineUnderTheBudgetField()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>());
        _handler.SetJsonResponse("/web-analytics?days=30", new AppWebAnalyticsSummaryDto
        {
            EstimatedStorageBytes = 1_048_576,
            StorageBudgetBytes = 2_097_152,
            StorageUsagePercent = 50,
            RejectedEvents = 3
        });

        var cut = Render<AppIngestionSettings>(p => p.Add(x => x.AppId, 7).Add(x => x.CanWrite, true));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".web-analytics-storage-usage")));
        Assert.Contains("AnalyticsStorageUsageLine", cut.Find(".web-analytics-storage-usage").TextContent, StringComparison.Ordinal);
        // Next to the budget it is measured against, in the same field group.
        Assert.True(cut.Markup.IndexOf("web-analytics-storage-usage", StringComparison.Ordinal)
                    > cut.Markup.IndexOf("analytics-storage-budget", StringComparison.Ordinal));
    }

    [Fact]
    public void R2007_AnUnreadableAudienceSummary_LeavesTheStorageLineOut()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>());
        _handler.SetResponse(HttpMethod.Get, "/web-analytics?days=30", HttpStatusCode.InternalServerError);

        var cut = Render<AppIngestionSettings>(p => p.Add(x => x.AppId, 7).Add(x => x.CanWrite, true));

        cut.WaitForAssertion(() => Assert.Contains("analytics-storage-budget", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".web-analytics-storage-usage"));
    }

    [Fact]
    public async Task GenerateKey_ShowsPlaintextOnce_NotifiesAndRaisesChanged()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/appmonitoring/apps/7/ingest-key",
            new IngestKeyResponse { Key = "aetheus-secret-once", CreatedAt = DateTime.UtcNow });
        var changes = 0;
        var cut = Render<AppIngestionSettings>(p => p
            .Add(x => x.AppId, 7)
            .Add(x => x.CanWrite, true)
            .Add(x => x.OnChanged, () => changes++));

        await InvokeAsync(cut, "GenerateKeyAsync");
        cut.Render();

        // The first generation replaces no key: nothing to confirm.
        Assert.Equal(0, _dialog.OpenCount);
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/appmonitoring/apps/7/ingest-key", StringComparison.Ordinal));
        Assert.Contains("aetheus-secret-once", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("OTEL_EXPORTER_OTLP_HEADERS", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(1, changes);
        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task RegenerateKey_Confirmed_AsksARedRegenerateThenReplacesTheKey()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/appmonitoring/apps/7/ingest-key",
            new IngestKeyResponse { Key = "aetheus-new-key", CreatedAt = DateTime.UtcNow });
        _dialog.ConfirmResult = true;
        var changes = 0;
        var cut = Render<AppIngestionSettings>(p => p
            .Add(x => x.AppId, 7)
            .Add(x => x.App, new MonitoredAppDto { Id = 7, Name = "API", HasIngestKey = true })
            .Add(x => x.CanWrite, true)
            .Add(x => x.OnChanged, () => changes++));

        await InvokeAsync(cut, "GenerateKeyAsync");
        cut.Render();

        Assert.Equal(1, _dialog.OpenCount);
        Assert.Equal("RegenerateIngestKeyConfirm", _dialog.LastConfirmMessage);
        var options = Assert.IsType<OmniConfirmOptions>(_dialog.LastConfirmOptions);
        Assert.True(options.Destructive);
        Assert.Equal("Regenerate", options.OkButtonText);
        Assert.Equal("GoBack", options.CancelButtonText);
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/appmonitoring/apps/7/ingest-key", StringComparison.Ordinal));
        Assert.Contains("aetheus-new-key", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task RegenerateKey_Dismissed_CallsNothing()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/appmonitoring/apps/7/ingest-key",
            new IngestKeyResponse { Key = "aetheus-new-key", CreatedAt = DateTime.UtcNow });
        _dialog.ConfirmResult = false;
        var changes = 0;
        var cut = Render<AppIngestionSettings>(p => p
            .Add(x => x.AppId, 7)
            .Add(x => x.App, new MonitoredAppDto { Id = 7, Name = "API", HasIngestKey = true })
            .Add(x => x.CanWrite, true)
            .Add(x => x.OnChanged, () => changes++));

        await InvokeAsync(cut, "GenerateKeyAsync");
        cut.Render();

        Assert.Equal(1, _dialog.OpenCount);
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST");
        Assert.DoesNotContain("aetheus-new-key", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, changes);
        Assert.Empty(Services.Toasts());
    }

    [Fact]
    public async Task GenerateKey_ApiFailure_ShowsErrorAndDoesNotRaiseChanged()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>());
        _handler.SetResponse(HttpMethod.Post, "api/appmonitoring/apps/7/ingest-key", HttpStatusCode.BadGateway);
        var changes = 0;
        var cut = Render<AppIngestionSettings>(p => p
            .Add(x => x.AppId, 7)
            .Add(x => x.CanWrite, true)
            .Add(x => x.OnChanged, () => changes++));

        await InvokeAsync(cut, "GenerateKeyAsync");

        Assert.Equal(0, changes);
        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
    }

    [Fact]
    public async Task AddThreshold_ValidatesThenPostsAndReloadsGrid()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/appmonitoring/apps/7/thresholds",
            new AppMetricThresholdDto { Id = 8, MonitoredAppId = 7, MetricName = "memory", Threshold = 2048 });
        var cut = Render<AppIngestionSettings>(p => p.Add(x => x.AppId, 7).Add(x => x.CanWrite, true));

        await InvokeAsync(cut, "AddThresholdAsync");
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST");
        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Warning);

        var request = (CreateMetricThresholdRequest)typeof(AppIngestionSettings)
            .GetField("_newThreshold", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        request.MetricName = "memory";
        request.Threshold = 2048;
        await InvokeAsync(cut, "AddThresholdAsync");

        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/appmonitoring/apps/7/thresholds", StringComparison.Ordinal));
        Assert.Equal(string.Empty, request.MetricName);
        Assert.Equal(0, request.Threshold);
    }

    [Fact]
    public async Task DeleteThreshold_UsesThresholdEndpointAndReloads()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>
        {
            new() { Id = 8, MonitoredAppId = 7, MetricName = "memory", Threshold = 2048 }
        });
        _handler.SetResponse(HttpMethod.Delete, "api/appmonitoring/thresholds/8", HttpStatusCode.NoContent);
        var cut = Render<AppIngestionSettings>(p => p.Add(x => x.AppId, 7).Add(x => x.CanWrite, true));

        await cut.InvokeAsync(() => (Task)typeof(AppIngestionSettings)
            .GetMethod("DeleteThresholdAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, [8])!);

        Assert.Contains(_handler.Requests, r => r.Method == "DELETE" && r.Url.EndsWith("api/appmonitoring/thresholds/8", StringComparison.Ordinal));
        Assert.True(_handler.Requests.Count(r => r.Method == "GET" && r.Url.Contains("/thresholds", StringComparison.Ordinal)) >= 2);
    }

    [Fact]
    public void ThresholdLoadFailure_IsContainedAndRendersEmptyState()
    {
        _handler.SetResponse(HttpMethod.Get, "api/appmonitoring/apps/7/thresholds", HttpStatusCode.ServiceUnavailable);

        var cut = Render<AppIngestionSettings>(p => p.Add(x => x.AppId, 7).Add(x => x.CanWrite, true));

        Assert.Contains("NoThresholds", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("AddThreshold", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OlderAppLoad_CannotReplaceCurrentAppThresholds()
    {
        var oldLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<List<AppMetricThresholdDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/appmonitoring/apps/7/thresholds", async ct =>
        {
            oldLoadStarted.TrySetResult();
            return await oldResponse.Task.WaitAsync(ct);
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/appmonitoring/apps/8/thresholds", new List<AppMetricThresholdDto>
        {
            new() { Id = 8, MonitoredAppId = 8, MetricName = "current-app" }
        });
        var cut = Render<AppIngestionSettings>(parameters => parameters.Add(component => component.AppId, 7));
        await oldLoadStarted.Task.WaitAsync(Aetheus.Front.Tests.TestDoubles.HangGuard.Limit, Xunit.TestContext.Current.CancellationToken);

        cut.Render(parameters => parameters.Add(component => component.AppId, 8));
        cut.WaitForAssertion(() => Assert.Contains("current-app", cut.Markup, StringComparison.Ordinal));

        oldResponse.SetResult([
            new AppMetricThresholdDto { Id = 7, MonitoredAppId = 7, MetricName = "old-app" }
        ]);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("current-app", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("old-app", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Collection(_handler.Requests.Where(request => request.Url.Contains("/thresholds", StringComparison.Ordinal)),
            oldRequest => Assert.EndsWith("api/appmonitoring/apps/7/thresholds", oldRequest.Url, StringComparison.Ordinal),
            currentRequest => Assert.EndsWith("api/appmonitoring/apps/8/thresholds", currentRequest.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveWebAnalyticsConfiguration_UsesDedicatedEndpointWithoutSecret()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/7/thresholds", new List<AppMetricThresholdDto>());
        _handler.SetJsonResponse(
            HttpMethod.Put,
            "api/appmonitoring/apps/7/web-analytics/configuration",
            new AppWebAnalyticsConfigurationDto
            {
                Enabled = true,
                PublicIngestEnabled = true,
                SiteId = "portfolio-prod",
                AllowedOrigins = ["https://portfolio.example"],
                StorageBudgetBytes = 134_217_728,
                PseudonymKeyVersion = 1
            });
        var changes = 0;
        var cut = Render<AppIngestionSettings>(parameters => parameters
            .Add(component => component.AppId, 7)
            .Add(component => component.CanWrite, true)
            .Add(component => component.OnChanged, () => changes++));
        SetField(cut.Instance, "_analyticsEnabled", true);
        SetField(cut.Instance, "_analyticsPublicIngestEnabled", true);
        SetField(cut.Instance, "_analyticsSiteId", "portfolio-prod");
        SetField(cut.Instance, "_analyticsOrigins", "https://portfolio.example");
        SetField(cut.Instance, "_analyticsStorageBudgetBytes", 134_217_728L);

        await InvokeAsync(cut, "SaveAnalyticsConfigurationAsync");

        Assert.Contains(_handler.Requests, request =>
            request.Method == "PUT"
            && request.Url.EndsWith(
                "api/appmonitoring/apps/7/web-analytics/configuration",
                StringComparison.Ordinal));
        // The save is followed by a read of the storage line, so the PUT is looked up by method.
        var body = _handler.RequestDetails.Last(request => request.Method == "PUT").Body;
        Assert.Contains("\"siteId\":\"portfolio-prod\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("pseudonym", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, changes);
    }

    private static Task InvokeAsync(IRenderedComponent<AppIngestionSettings> cut, string method) => cut.InvokeAsync(() =>
        (Task)typeof(AppIngestionSettings).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, [])!);

    private static void SetField<T>(AppIngestionSettings instance, string name, T value) =>
        typeof(AppIngestionSettings)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(instance, value);
}
