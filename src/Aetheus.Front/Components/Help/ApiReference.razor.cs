// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;

namespace Aetheus.Front.Components.Help;

public partial class ApiReference
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    private sealed record Endpoint(string Method, string Path, string Summary, string Tag);

    // The OpenAPI document is static for the lifetime of a backend build, so the parsed endpoints
    // are cached: re-entering the page renders instantly instead of re-fetching and re-parsing the
    // spec on every navigation. APR9: the key is centralised in ListCacheService so the version-check
    // loop invalidates it on a detected redeploy - the next visit then transparently refetches the
    // updated spec (automatic, no manual "refresh" button).
    private const string CacheKey = ListCacheService.ApiReferenceKey;

    private readonly List<Endpoint> _endpoints = [];
    private bool _loading = true;
    // The OpenAPI endpoint is admin-gated server-side: a non-admin's fetch returns 403. Surfaced as an
    // explicit "admin only" state rather than the generic empty one. Driven by the server response, not
    // a front-side role check, so it is immune to the auth-state load race during boot.
    private bool _forbidden;
    private string _search = string.Empty;

    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];

    private IEnumerable<IGrouping<string, Endpoint>> FilteredGroups =>
        _endpoints
            .Where(e => string.IsNullOrWhiteSpace(_search)
                || e.Path.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || e.Summary.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Tag)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    protected override async Task OnInitializedAsync()
    {
        if (Cache.TryGet<List<Endpoint>>(CacheKey, out var cached) && cached is not null)
        {
            _endpoints.AddRange(cached);
            _loading = false;
            return;
        }

        try
        {
            // FRONT-020: a spec that arrives after a sign-out is neither cached nor shown.
            var ticket = Cache.Begin();
            using var doc = await Api.Servers.GetOpenApiSpecAsync();
            if (doc is not null && Cache.IsCurrent(ticket))
            {
                Parse(doc);
                Cache.TryStore(ticket, CacheKey, _endpoints.ToList());
            }
        }
        catch (HttpRequestException ex)
        {
            // 403 ⇒ caller is not an admin (real gating lives on the server); anything else ⇒ the spec is
            // simply unavailable. Distinguish so a non-admin sees an honest "admin only" message.
            _forbidden = ex.StatusCode == HttpStatusCode.Forbidden;
        }
        _loading = false;
    }

    // Walk the OpenAPI document: paths → per-method operation (summary + first tag).
    private void Parse(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("paths", out var paths)) return;

        foreach (var path in paths.EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                if (!HttpMethods.Contains(op.Name, StringComparer.OrdinalIgnoreCase)) continue;

                var summary = op.Value.TryGetProperty("summary", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                var tag = op.Value.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0
                    ? tags[0].GetString() ?? L["ApiReferenceGeneralTag"]
                    : DeriveTagFromPath(path.Name);

                _endpoints.Add(new Endpoint(op.Name.ToUpperInvariant(), path.Name, summary, tag));
            }
        }
    }

    // Fallback grouping when an operation declares no tag: the first path segment after /api.
    private string DeriveTagFromPath(string path)
    {
        var segments = path.Trim('/').Split('/');
        var idx = segments.Length > 0 && segments[0] == "api" ? 1 : 0;
        return idx < segments.Length ? segments[idx] : L["ApiReferenceGeneralTag"];
    }
}
