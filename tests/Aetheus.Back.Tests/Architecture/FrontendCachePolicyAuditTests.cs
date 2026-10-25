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

        // The HTML shell is revalidated, never forbidden from storage: no-store keeps the page out of
        // the browser's back/forward cache (user-approved policy, 2026-10-22). Pragma and Expires are
        // dropped for it.
        var html = Branch(program, "if (isHtml)", "else if (isServiceWorker || isRuntimeSettings)");
        Assert.Contains("Headers.CacheControl = \"no-cache\";", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"no-store", html, StringComparison.Ordinal);
        Assert.DoesNotContain("must-revalidate\"", html, StringComparison.Ordinal);
        Assert.Contains("Headers.Remove(\"Pragma\")", html, StringComparison.Ordinal);
        Assert.Contains("Headers.Remove(\"Expires\")", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Headers.Pragma =", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Headers.Expires =", html, StringComparison.Ordinal);

        // The service worker and the runtime settings keep the strict policy.
        var strict = Branch(program, "else if (isServiceWorker || isRuntimeSettings)", "else if (isFramework)");
        Assert.Contains("Headers.CacheControl = \"no-store, no-cache, must-revalidate\";", strict, StringComparison.Ordinal);
        Assert.Contains("Headers.Pragma = \"no-cache\"", strict, StringComparison.Ordinal);
        Assert.Contains("Headers.Expires = \"0\"", strict, StringComparison.Ordinal);
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

        // Cache policy is verified at the static server that serves the frontend.
        Assert.DoesNotContain("style-src 'self' 'unsafe-inline'", program, StringComparison.Ordinal);
        // The shell forbids inline style elements; Monaco owns its separate document policy.
        Assert.Contains("\"style-src-elem 'self'; \"", program, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-inline'; \" +", program, StringComparison.Ordinal);
        var api = File.ReadAllText(Path.Combine(root, "src", "Aetheus.Back", "Extensions", "SecurityHeadersExtensions.cs"));
        Assert.Contains("\"style-src 'self'; \"", api, StringComparison.Ordinal);
        Assert.DoesNotContain("style-src 'self' 'unsafe-inline'", api, StringComparison.Ordinal);
        // Style attributes are forbidden by CSP, so markup must not introduce them.
        Assert.Contains("style-src-attr 'none'", program, StringComparison.Ordinal);
        Assert.DoesNotContain("style-src-attr 'unsafe-inline'", program, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", index, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The shell and Monaco documents report policy violations through report-to and
    /// Reporting-Endpoints.</summary>
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

    private static string Branch(string program, string start, string end)
    {
        var from = program.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"The cache policy no longer has the branch '{start}'.");
        var to = program.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"The cache policy branch '{start}' no longer ends at '{end}'.");
        return program[from..to];
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aetheus.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
