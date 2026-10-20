// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-idle-frontend</c> function, port of the idle-frontend checks of
/// <c>deploy/scripts/release-fast-deploy.sh</c>, the ones run 2247 needed: that release went live with
/// an index.html still carrying the SDK's <c>#[.{fingerprint}]</c> placeholders, and every smoke check
/// passed because the page contained <c>&lt;html</c> and a versioned component-library script. Same
/// checks, same order, same messages as the script:
/// <list type="number">
/// <item>the index answers (<c>curl -fsS</c>) and contains <c>&lt;html</c>, case-insensitive;</item>
/// <item>it contains no <c>#[</c>;</item>
/// <item>it names a <c>blazor.webassembly*.js</c> script (the last <c>src="..."</c> of the first line
/// naming one, as <c>sed</c> then <c>head -n 1</c> pick it), and a HEAD request on it succeeds
/// (<c>curl -fsSI</c>, redirects not followed);</item>
/// <item>it references <c>_content/OmniEurope.Blazor/omnieurope.blazor.css</c>, which contains
/// <c>.omni-</c>, and <c>_content/OmniEurope.Blazor/omniInterop.js</c> contains
/// <c>focusFirstInvalid</c>;</item>
/// <item>a HEAD request on the index carries a <c>Cache-Control</c> header containing <c>no-store</c>.</item>
/// </list>
/// Asset paths are appended to the URL as the script appended them to <c>FRONT_URL</c>.
///
/// Differences, deliberate: <c>FRONT_URL</c> is the required <c>url</c> parameter, given a trailing
/// slash when it has none (the script always built it with one). An index that does not answer, or a
/// HEAD on it that fails, is refused with a message naming the status, where the script stopped on
/// <c>set -e</c> with curl's own error.
/// </summary>
public sealed partial class VerifyIdleFrontendFunction : IPipelineFunction
{
    private const string Unchanged = "; Apache was not changed.";
    private const string OmniInteropPath = "_content/OmniEurope.Blazor/omniInterop.js";

    private static readonly HttpClient s_http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _http;

    public VerifyIdleFrontendFunction()
        : this(s_http)
    {
    }

    internal VerifyIdleFrontendFunction(HttpClient http) => _http = http;

    public string Name => PipelineFunctionCatalog.VerifyIdleFrontend.Name;

    // The script's sed expressions, unchanged: the greedy prefix keeps the last match of a line.
    [GeneratedRegex("^.*src=\"([^\"]*blazor\\.webassembly[^\"]*\\.js)\".*$")]
    private static partial Regex BlazorBootScript();

    [GeneratedRegex("^.*href=\"([^\"]*_content/OmniEurope\\.Blazor/omnieurope\\.blazor\\.css)\".*$")]
    private static partial Regex OmniStylesheet();

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Required and non-blank: the executor refuses the step as invalid-request otherwise.
        var url = context.Parameters["url"];
        if (!url.EndsWith('/'))
            url += "/";

        var index = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (index is null)
            return PipelineFunctionResult.Failure($"Idle frontend {url} did not answer" + Unchanged);
        var failure = CheckIndex(index)
            ?? await CheckBootstrapAsync(url, index, cancellationToken).ConfigureAwait(false)
            ?? await CheckOmniAssetsAsync(url, index, cancellationToken).ConfigureAwait(false)
            ?? await CheckNoStoreAsync(url, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return PipelineFunctionResult.Failure(failure);

        await context.Log($">>> Idle frontend {url} serves a resolved index, its Blazor bootstrap script and its OmniEurope.Blazor assets, uncached.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    private static string? CheckIndex(string index)
    {
        if (!index.Contains("<html", StringComparison.OrdinalIgnoreCase))
            return "Idle frontend is not serving HTML" + Unchanged;
        if (index.Contains("#[", StringComparison.Ordinal))
            return "Idle frontend index.html still contains unresolved asset placeholders" + Unchanged;
        return null;
    }

    private async Task<string?> CheckBootstrapAsync(string url, string index, CancellationToken cancellationToken)
    {
        var bootPath = FirstCapture(BlazorBootScript(), index);
        if (bootPath is null)
            return "Idle frontend index.html does not reference the Blazor bootstrap script" + Unchanged;
        return await HeadAsync(url + bootPath, cancellationToken).ConfigureAwait(false) is null
            ? $"Idle frontend references a Blazor bootstrap script it does not serve ({bootPath})" + Unchanged
            : null;
    }

    private async Task<string?> CheckOmniAssetsAsync(string url, string index, CancellationToken cancellationToken)
    {
        var stylesheetPath = FirstCapture(OmniStylesheet(), index);
        if (stylesheetPath is null)
            return "Idle frontend does not reference the OmniEurope.Blazor stylesheet" + Unchanged;
        var stylesheet = await GetAsync(url + stylesheetPath, cancellationToken).ConfigureAwait(false);
        var compatible = stylesheet is not null && stylesheet.Contains(".omni-", StringComparison.Ordinal)
            && await GetAsync(url + OmniInteropPath, cancellationToken).ConfigureAwait(false) is { } interop
            && interop.Contains("focusFirstInvalid", StringComparison.Ordinal);
        return compatible ? null : "Idle frontend OmniEurope.Blazor assets are missing or incompatible" + Unchanged;
    }

    private async Task<string?> CheckNoStoreAsync(string url, CancellationToken cancellationToken)
    {
        var cacheControl = await HeadAsync(url, cancellationToken).ConfigureAwait(false);
        if (cacheControl is null)
            return $"Idle frontend {url} did not answer a HEAD request" + Unchanged;
        return cacheControl.Any(value => value.Contains("no-store", StringComparison.OrdinalIgnoreCase))
            ? null
            : "Idle frontend index.html is cacheable" + Unchanged;
    }

    /// <summary>The first line holding a match, as <c>sed -n 's/.../\1/p' | head -n 1</c> reads it.</summary>
    private static string? FirstCapture(Regex pattern, string text) =>
        text.Split('\n')
            .Select(line => pattern.Match(line))
            .FirstOrDefault(match => match.Success)?.Groups[1].Value;

    /// <summary><c>curl -fsS</c>: the body of a successful answer, or null on an HTTP error or a
    /// connection failure.</summary>
    private async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            return (int)response.StatusCode < 400
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary><c>curl -fsSI</c>: the Cache-Control values of a successful answer to a HEAD request
    /// (empty when it sends none), or null on an HTTP error or a connection failure.</summary>
    private async Task<IReadOnlyList<string>?> HeadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 400)
                return null;
            return response.Headers.NonValidated.TryGetValues("Cache-Control", out var values) ? [.. values] : [];
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
