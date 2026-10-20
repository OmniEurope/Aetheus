// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Help;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Help;

public class ApiReferenceTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ApiReferenceTests() => _handler = BunitTestHelper.RegisterServices(this);

    // Minimal OpenAPI document. GetOpenApiSpecAsync does http.GetStringAsync("openapi/v1.json"),
    // so the mocked JSON body is parsed verbatim by JsonDocument.Parse.
    private static Dictionary<string, object> Spec() => new()
    {
        ["paths"] = new Dictionary<string, object>
        {
            ["/api/servers"] = new Dictionary<string, object>
            {
                ["get"] = new { summary = "List servers", tags = new[] { "Servers" } },
                ["post"] = new { summary = "Create server", tags = new[] { "Servers" } }
            },
            ["/api/projects"] = new Dictionary<string, object>
            {
                ["get"] = new { summary = "List projects", tags = new[] { "Projects" } }
            }
        }
    };

    [Fact]
    public void Renders_ParsedEndpoints_GroupedByTag()
    {
        _handler.SetJsonResponse("openapi/v1.json", Spec());

        var cut = Render<ApiReference>();

        cut.WaitForState(() => cut.Markup.Contains("/api/servers"));
        Assert.Contains("/api/servers", cut.Markup);
        Assert.Contains("/api/projects", cut.Markup);
        Assert.Contains("Servers", cut.Markup);
    }

    [Fact]
    public void Renders_SpecUnavailable_ShowsEmptyState()
    {
        // GetStringAsync throws on a non-success status → component renders its empty state.
        _handler.SetResponse("openapi/v1.json", HttpStatusCode.ServiceUnavailable);

        var cut = Render<ApiReference>();

        Assert.DoesNotContain("/api/servers", cut.Markup);
        // A non-403 failure is the generic "unavailable" state, NOT the admin-only one.
        Assert.DoesNotContain("ApiReferenceAdminOnly", cut.Markup);
    }

    [Fact]
    public void Renders_Forbidden_ShowsAdminOnlyState()
    {
        // The OpenAPI endpoint is admin-gated server-side: a non-admin's fetch returns 403, which the
        // page surfaces as an explicit "admin only" message (the stub localizer echoes the resx key).
        _handler.SetResponse("openapi/v1.json", HttpStatusCode.Forbidden);

        var cut = Render<ApiReference>();

        Assert.Contains("ApiReferenceAdminOnly", cut.Markup);
        Assert.DoesNotContain("/api/servers", cut.Markup);
    }

    [Fact]
    public void AnUntaggedOperation_IsGroupedUnderItsFirstPathSegmentAfterApi()
    {
        _handler.SetJsonResponse("openapi/v1.json", new Dictionary<string, object>
        {
            ["paths"] = new Dictionary<string, object>
            {
                ["/api/widgets/{id}"] = new Dictionary<string, object> { ["get"] = new { summary = "One widget" } },
                ["/health/live"] = new Dictionary<string, object> { ["get"] = new { summary = "Liveness" } },
                ["/api"] = new Dictionary<string, object> { ["get"] = new { summary = "Root" } },
                ["/api/servers"] = new Dictionary<string, object> { ["get"] = new { summary = "Tagged", tags = Array.Empty<string>() } }
            }
        });

        var cut = Render<ApiReference>();

        cut.WaitForState(() => cut.Markup.Contains("/api/widgets/{id}", StringComparison.Ordinal));
        var headings = cut.FindAll("h5").Select(heading => heading.TextContent.Trim()).ToList();
        Assert.Equal(["ApiReferenceGeneralTag", "health", "servers", "widgets"], headings);
    }
}
