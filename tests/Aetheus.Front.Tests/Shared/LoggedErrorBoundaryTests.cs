// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// Recette R2-100: a browser error reached the system logs as "Client error at : ... ObjectDisposedException
/// - CancellationTokenSource_Disposed", with no page and no call stack, so the failing screen could not
/// be named. The report now carries the page's path (never its query string) and the call stack.
/// </summary>
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

    private static async Task<string> ReportBodyAsync(BunitTestHelper.TestHandler handler)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var report = handler.RequestDetails.ToList().FirstOrDefault(request => request.Url.Contains("api/client-errors"));
            if (report.Body is not null)
                return report.Body;
            await Task.Delay(10);
        }

        throw new TimeoutException("No client error report was posted.");
    }

    private sealed class ThrowingChild : ComponentBase
    {
        protected override void OnInitialized() =>
            throw new ObjectDisposedException(null, "CancellationTokenSource_Disposed");
    }
}
