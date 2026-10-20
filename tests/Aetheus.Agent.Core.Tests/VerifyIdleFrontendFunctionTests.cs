// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the idle-frontend checks of <c>deploy/scripts/release-fast-deploy.sh</c>, run through
/// the real executor as a deployment function against an in-memory frontend. The incident 2247 refusals
/// pinned by <c>DeploymentSafetyAuditTests.FastRelease_RefusesAnIndexWhoseAssetsWereNeverResolved</c>
/// (an unresolved placeholder, a Blazor bootstrap script named but not served) come first; the other
/// checks of the same block follow, each with the script's message.
/// </summary>
public sealed class VerifyIdleFrontendFunctionTests : IDisposable
{
    private const string Url = "http://127.0.0.1:18081/";
    private const string Unchanged = "; Apache was not changed.";
    private const string Prefix = "Function 'verify-idle-frontend' failed: ";

    private const string ResolvedIndex = """
        <!DOCTYPE html>
        <html lang="en">
        <head><link href="_content/OmniEurope.Blazor/omnieurope.blazor.css" rel="stylesheet" /></head>
        <body><script src="_framework/blazor.webassembly.abc123.js"></script></body>
        </html>
        """;

    private readonly FunctionTestHarness _harness = new("aetheus-idle-front");
    private readonly FakeFrontend _front = new();
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal) { ["url"] = Url };

    public VerifyIdleFrontendFunctionTests()
    {
        _front.Get[Url] = ResolvedIndex;
        _front.Head[Url] = "no-cache, no-store, must-revalidate";
        _front.Head[Url + "_framework/blazor.webassembly.abc123.js"] = null;
        _front.Get[Url + "_content/OmniEurope.Blazor/omnieurope.blazor.css"] = ".omni-button{}";
        _front.Get[Url + "_content/OmniEurope.Blazor/omniInterop.js"] = "export function focusFirstInvalid(){}";
    }

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters)
        };
        return _harness.RunAsync(new VerifyIdleFrontendFunction(new HttpClient(_front)), env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.StartsWith(Prefix, result.FailureReason);
        return result.FailureReason![Prefix.Length..];
    }

    [Fact]
    public async Task ResolvedIndex_WithItsAssetsAndNoStore_IsAccepted()
    {
        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(variables);
        Assert.Equal(
            [
                "GET " + Url, "HEAD " + Url + "_framework/blazor.webassembly.abc123.js",
                "GET " + Url + "_content/OmniEurope.Blazor/omnieurope.blazor.css",
                "GET " + Url + "_content/OmniEurope.Blazor/omniInterop.js", "HEAD " + Url
            ],
            _front.Requests);
    }

    [Fact]
    public async Task Incident2247_AnIndexWithUnresolvedPlaceholders_IsRefusedBeforeAnyAssetIsFetched()
    {
        _front.Get[Url] = ResolvedIndex.Replace("blazor.webassembly.abc123.js", "blazor.webassembly#[.{fingerprint}].js", StringComparison.Ordinal);

        Assert.Equal("Idle frontend index.html still contains unresolved asset placeholders" + Unchanged, await FailureAsync());
        Assert.Equal(["GET " + Url], _front.Requests);
    }

    [Fact]
    public async Task Incident2247_ABootstrapScriptNamedButNotServed_IsRefused()
    {
        _front.Head.Remove(Url + "_framework/blazor.webassembly.abc123.js");

        Assert.Equal(
            "Idle frontend references a Blazor bootstrap script it does not serve (_framework/blazor.webassembly.abc123.js)" + Unchanged,
            await FailureAsync());
    }

    [Fact]
    public async Task AnIndexNamingNoBootstrapScript_IsRefused()
    {
        _front.Get[Url] = ResolvedIndex.Replace("blazor.webassembly.abc123.js", "app.js", StringComparison.Ordinal);

        Assert.Equal("Idle frontend index.html does not reference the Blazor bootstrap script" + Unchanged, await FailureAsync());
    }

    [Fact]
    public async Task TheBootstrapScript_IsTheLastOfTheFirstLineThatNamesOne()
    {
        _front.Get[Url] = ResolvedIndex.Replace(
            "<script src=\"_framework/blazor.webassembly.abc123.js\"></script>",
            "<script src=\"x/blazor.webassembly.old.js\"></script><script src=\"_framework/blazor.webassembly.abc123.js\"></script>\n<script src=\"y/blazor.webassembly.js\"></script>",
            StringComparison.Ordinal);

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("HEAD " + Url + "_framework/blazor.webassembly.abc123.js", _front.Requests);
    }

    [Theory]
    [InlineData("<body>no markup</body>")]
    [InlineData("")]
    public async Task AnIndexWithoutHtml_IsRefused(string body)
    {
        _front.Get[Url] = body;

        Assert.Equal("Idle frontend is not serving HTML" + Unchanged, await FailureAsync());
    }

    [Fact]
    public async Task AnUppercaseHtmlTag_IsHtml()
    {
        _front.Get[Url] = ResolvedIndex.Replace("<html", "<HTML", StringComparison.Ordinal);

        Assert.Equal(0, (await RunAsync()).Result.ExitCode);
    }

    [Fact]
    public async Task AnIndexThatDoesNotAnswer_IsRefused()
    {
        _front.Get.Remove(Url);

        Assert.Equal($"Idle frontend {Url} did not answer" + Unchanged, await FailureAsync());
    }

    [Fact]
    public async Task AMissingOmniStylesheetReference_IsRefused()
    {
        _front.Get[Url] = ResolvedIndex.Replace("omnieurope.blazor.css", "site.css", StringComparison.Ordinal);

        Assert.Equal("Idle frontend does not reference the OmniEurope.Blazor stylesheet" + Unchanged, await FailureAsync());
    }

    [Theory]
    [InlineData("_content/OmniEurope.Blazor/omnieurope.blazor.css", "body{}")]
    [InlineData("_content/OmniEurope.Blazor/omnieurope.blazor.css", null)]
    [InlineData("_content/OmniEurope.Blazor/omniInterop.js", "export function other(){}")]
    [InlineData("_content/OmniEurope.Blazor/omniInterop.js", null)]
    public async Task MissingOrIncompatibleOmniAssets_AreRefused(string path, string? content)
    {
        if (content is null)
            _front.Get.Remove(Url + path);
        else
            _front.Get[Url + path] = content;

        Assert.Equal("Idle frontend OmniEurope.Blazor assets are missing or incompatible" + Unchanged, await FailureAsync());
    }

    [Theory]
    [InlineData("public, max-age=3600")]
    [InlineData(null)]
    public async Task ACacheableIndex_IsRefused(string? cacheControl)
    {
        _front.Head[Url] = cacheControl;

        Assert.Equal("Idle frontend index.html is cacheable" + Unchanged, await FailureAsync());
    }

    [Fact]
    public async Task AUrlWithoutTrailingSlash_GetsOne()
    {
        _parameters["url"] = Url.TrimEnd('/');

        Assert.Equal(0, (await RunAsync()).Result.ExitCode);
        Assert.Equal("GET " + Url, _front.Requests[0]);
    }

    /// <summary>A frontend answering GET with a body and HEAD with an optional Cache-Control value; any
    /// other request is a 404.</summary>
    private sealed class FakeFrontend : HttpMessageHandler
    {
        public Dictionary<string, string> Get { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> Head { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add($"{request.Method} {url}");
            var response = new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get && Get.TryGetValue(url, out var body))
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            else if (request.Method == HttpMethod.Head && Head.TryGetValue(url, out var cacheControl))
            {
                response = new HttpResponseMessage(HttpStatusCode.OK);
                if (cacheControl is not null)
                    response.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
            }
            return Task.FromResult(response);
        }
    }
}
