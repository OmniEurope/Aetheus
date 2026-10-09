// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Architecture;

public sealed class FrontendCachePolicyAuditTests
{
    [Fact]
    public void StaticServer_PreventsDeploymentBoundHtmlShellFromBeingCached()
    {
        var program = File.ReadAllText(Path.Combine(
            RepoRoot(), "deploy", "docker", "StaticServer.Program.cs"));

        Assert.Contains("text/html", program, StringComparison.Ordinal);
        Assert.Contains("/_content/OmniEurope.Blazor", program, StringComparison.Ordinal);
        Assert.Contains("no-store, no-cache, must-revalidate", program, StringComparison.Ordinal);
        Assert.Contains("Headers.Pragma = \"no-cache\"", program, StringComparison.Ordinal);
        Assert.Contains("Headers.Expires = \"0\"", program, StringComparison.Ordinal);
        Assert.True(
            program.IndexOf("app.Use(async", StringComparison.Ordinal)
            < program.IndexOf("app.UseBlazorFrameworkFiles()", StringComparison.Ordinal),
            "The cache policy must run before static and fallback responses are produced.");
    }

    [Fact]
    public void StaticServer_EmitsSecurityHeadersWithoutDependingOnApache()
    {
        var program = File.ReadAllText(Path.Combine(
            RepoRoot(), "deploy", "docker", "StaticServer.Program.cs"));

        Assert.Contains("Headers[\"X-Content-Type-Options\"] = \"nosniff\"", program, StringComparison.Ordinal);
        Assert.Contains("Headers[\"X-Frame-Options\"] = \"DENY\"", program, StringComparison.Ordinal);
        Assert.Contains("Headers[\"Content-Security-Policy\"] = isMonacoFrame", program, StringComparison.Ordinal);
        Assert.Contains("? MonacoFramePolicy.Build(cspReportUri)", program, StringComparison.Ordinal);
        Assert.Contains(": contentSecurityPolicy;", program, StringComparison.Ordinal);
        Assert.Contains("Headers[\"X-Frame-Options\"] = \"SAMEORIGIN\"", program, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", program, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", program, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", program, StringComparison.Ordinal);
        Assert.True(
            program.IndexOf("Headers[\"X-Content-Type-Options\"]", StringComparison.Ordinal)
            < program.IndexOf("app.UseBlazorFrameworkFiles()", StringComparison.Ordinal),
            "Security headers must be registered before static and fallback responses are produced.");
    }

    [Fact]
    public void StaticServer_AuthorizesPublishedInlineScriptsByHashWithoutUnsafeInline()
    {
        var program = File.ReadAllText(Path.Combine(
            RepoRoot(), "deploy", "docker", "StaticServer.Program.cs"));

        Assert.Contains("Regex.Matches", program, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData", program, StringComparison.Ordinal);
        Assert.Contains("'sha256-{hash}'", program, StringComparison.Ordinal);
        Assert.Contains("inlineScriptHashes.Distinct()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_Csp_AllowsOnlyDynamicStyleElementsAndAttributes()
    {
        var root = RepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "deploy", "docker", "StaticServer.Program.cs"));
        var index = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Front", "wwwroot", "index.html"));

        // Recette R-250: deploy/apache/aetheus.conf, a vhost no pipeline ever installed, is gone with
        // its copy of the policy; the policy the site serves is the static server's, checked here.
        Assert.DoesNotContain("style-src 'self' 'unsafe-inline'", program, StringComparison.Ordinal);
        // Recette R-526: no inline style element either, now that Monaco has its own document and policy.
        Assert.Contains("\"style-src-elem 'self'; \"", program, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-inline'; \" +", program, StringComparison.Ordinal);
        var api = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Back", "Extensions", "SecurityHeadersExtensions.cs"));
        Assert.Contains("\"style-src 'self'; \"", api, StringComparison.Ordinal);
        Assert.DoesNotContain("style-src 'self' 'unsafe-inline'", api, StringComparison.Ordinal);
        // PLAN-008 lot 44: style attributes are refused outright. The last three dependencies were
        // lifted rather than excused, so this is a fact about the product, not a preference: a style
        // attribute reappearing in markup breaks the page it is on.
        Assert.Contains("style-src-attr 'none'", program, StringComparison.Ordinal);
        Assert.DoesNotContain("style-src-attr 'unsafe-inline'", program, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", index, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PLAN-003 lot 14 / D16: a violation on a FRONT page is refused by the front document's policy,
    /// which is this one. Until it named a collector, those violations were reported nowhere, and the
    /// blocked inline script on a run page could not be identified at all.
    ///
    /// Recette R2-108 (ZAP 10055-3, "CSP: Notices", finding 1229): the collector is named by report-to
    /// and the Reporting-Endpoints header, never by report-uri, which the scanner's CSP parser reports
    /// as deprecated on its own. Both front documents (the shell and the Monaco frame) say it the same way.
    /// </summary>
    [Fact]
    public void Frontend_Csp_ReportsItsViolationsToTheApiCollector()
    {
        var root = RepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "deploy", "docker", "StaticServer.Program.cs"));
        var reporting = File.ReadAllText(Path.Combine(root, "deploy", "docker", "CspReporting.cs"));
        var monaco = File.ReadAllText(Path.Combine(root, "deploy", "docker", "MonacoFramePolicy.cs"));

        Assert.Contains("/api/security/csp-report", program, StringComparison.Ordinal);

        // Read the policy expression itself, not the comments around it.
        var start = program.IndexOf("var contentSecurityPolicy =", StringComparison.Ordinal);
        Assert.True(start >= 0, "The policy is no longer built where this guard reads it.");
        var end = program.IndexOf("// QA scans this container", start, StringComparison.Ordinal);
        Assert.True(end > start, "The policy expression no longer ends where this guard expects.");
        var policy = program[start..end];
        Assert.Contains("CspReporting.Directive(cspReportUri)", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("report-uri", policy, StringComparison.Ordinal);
        Assert.Contains("CspReporting.Directive(reportUri)", monaco, StringComparison.Ordinal);
        Assert.DoesNotContain("report-uri {", monaco, StringComparison.Ordinal);
        Assert.Contains("$\"; report-to {EndpointName}\"", reporting, StringComparison.Ordinal);
        Assert.DoesNotContain("report-uri {", reporting, StringComparison.Ordinal);

        // The endpoint the report-to name refers to is sent with every response that carries a policy.
        Assert.Contains("$\"{CspReporting.EndpointName}=\\\"{cspReportUri}\\\"\"", program, StringComparison.Ordinal);
        Assert.Contains("Headers[\"Reporting-Endpoints\"] = reportingEndpoints", program, StringComparison.Ordinal);
        Assert.True(
            program.IndexOf("Headers[\"Reporting-Endpoints\"]", StringComparison.Ordinal)
            < program.IndexOf("app.UseBlazorFrameworkFiles()", StringComparison.Ordinal),
            "The reporting endpoint must be registered before static and fallback responses are produced.");

        // The route has to be the one the controller actually serves, or the reports land on a 404.
        var controller = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Back", "Components", "Security", "CspReportController.cs"));
        Assert.Contains("[Route(\"api/security\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"csp-report\")]", controller, StringComparison.Ordinal);
        Assert.Contains("\"application/reports+json\"", controller, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aetheus.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
