// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Client-error reports include page path and stack trace while omitting query
/// strings.</summary>
public sealed class LoggedErrorBoundaryTests : BunitContext
{
    [Fact]
    public async Task AnUnhandledException_IsReportedWithThePagePathAndTheCallStack()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse(HttpMethod.Post, "api/client-errors", HttpStatusCode.OK);
        Services.GetRequiredService<NavigationManager>().NavigateTo("pipelines/runs/7?tab=logs&token=s3cr3t#step");

        Render<LoggedErrorBoundary>(parameters => parameters
            .Add(boundary => boundary.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<ThrowingChild>(0);
                builder.CloseComponent();
            })));

        var body = await ReportBodyAsync(handler);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("/pipelines/runs/7", json.RootElement.GetProperty("path").GetString());
        Assert.Equal("Unhandled client exception: ObjectDisposedException", json.RootElement.GetProperty("summary").GetString());
        Assert.Contains(nameof(ThrowingChild), json.RootElement.GetProperty("stackTrace").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", body, StringComparison.Ordinal);
        // No level: the back end writes it as an error, as every report was before R1-89.
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("level").ValueKind);
    }

    [Fact]
    public async Task AnErrorOnTheDashboard_NamesTheRootPage_NotAnEmptyAddress()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse(HttpMethod.Post, "api/client-errors", HttpStatusCode.OK);
        Services.GetRequiredService<NavigationManager>().NavigateTo("");

        Render<LoggedErrorBoundary>(parameters => parameters
            .Add(boundary => boundary.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<ThrowingChild>(0);
                builder.CloseComponent();
            })));

        using var json = JsonDocument.Parse(await ReportBodyAsync(handler));
        Assert.Equal("/", json.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task AVeryLongStack_IsBoundedTo4000Characters()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse(HttpMethod.Post, "api/client-errors", HttpStatusCode.OK);

        Services.GetRequiredService<ClientErrorReporter>().Report("abc", "boom", "details", new string('x', 9000));

        using var json = JsonDocument.Parse(await ReportBodyAsync(handler));
        Assert.Equal(4000, json.RootElement.GetProperty("stackTrace").GetString()!.Length);
    }

    // The report is posted in the background: the test waits for that request, which the handler records
    // with its body in the same step.
    private static async Task<string> ReportBodyAsync(BunitTestHelper.TestHandler handler)
    {
        await handler.WhenRequestedAsync(
            request => request.Url.Contains("api/client-errors", StringComparison.Ordinal), 1, "the client error report");
        var report = handler.RequestDetails.ToList().First(request => request.Url.Contains("api/client-errors", StringComparison.Ordinal));
        Assert.NotNull(report.Body);
        return report.Body;
    }

    private sealed class ThrowingChild : ComponentBase
    {
        protected override void OnInitialized() =>
            throw new ObjectDisposedException(null, "CancellationTokenSource_Disposed");
    }
}
