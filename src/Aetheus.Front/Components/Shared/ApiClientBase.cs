// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The transport every API sub-client shares: verb helpers that map an HTTP response onto the
/// front-end result types, plus the two pagination query builders. It holds no endpoint knowledge -
/// each sub-client owns its own URLs - and no state, so sub-clients stay independent of each other.
/// </summary>
public abstract class ApiClientBase(HttpClient http)
{
    /// <summary>The shared, handler-wrapped client. Sub-clients read it; nobody replaces it.</summary>
    protected HttpClient Http { get; } = http;

    /// <summary>POST a JSON body, deserialize the JSON response. Returns <c>default</c> on non-2xx.</summary>
    protected async Task<TRes?> PostJsonAsync<TReq, TRes>(string url, TReq body, CancellationToken ct = default)
    {
        var response = await Http.PostAsJsonAsync(url, body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<TRes>(JsonOptions.Web, ct).ConfigureAwait(false);
    }


    /// <summary>
    /// POST a JSON body and preserve the API error payload for callers that need to render the
    /// rejection reason inline instead of reducing every non-2xx response to a null result.
    /// </summary>
    protected async Task<ApiOutcome<TValue, ApiError>> PostForApiErrorOutcomeAsync<TRequest, TValue>(
        string url, TRequest body, CancellationToken ct = default)
        where TValue : class
    {
        var response = await Http.PostAsJsonAsync(url, body, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<TValue>(JsonOptions.Web, ct).ConfigureAwait(false);
            return new(value, null, false, response.StatusCode);
        }

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ApiError? error = null;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                error = payload[0] == '"'
                    ? new ApiError { Message = JsonSerializer.Deserialize<string>(payload, JsonOptions.Web) ?? string.Empty }
                    : JsonSerializer.Deserialize<ApiError>(payload, JsonOptions.Web);
            }
            catch (JsonException)
            {
                error = new ApiError { Message = payload };
            }
        }
        return new(null, error, response.StatusCode == HttpStatusCode.NotFound, response.StatusCode);
    }


    /// <summary>PUT a JSON body, deserialize the JSON response. Returns <c>default</c> on non-2xx.</summary>
    protected async Task<TRes?> PutJsonAsync<TReq, TRes>(string url, TReq body, CancellationToken ct = default)
    {
        var response = await Http.PutAsJsonAsync(url, body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<TRes>(JsonOptions.Web, ct).ConfigureAwait(false);
    }


    /// <summary>POST a JSON body, return the request status without parsing the response.</summary>
    protected async Task<ApiStatus> PostJsonNoBodyAsync<TReq>(string url, TReq body, CancellationToken ct = default)
    {
        var response = await Http.PostAsJsonAsync(url, body, ct).ConfigureAwait(false);
        return ApiStatus.From(response);
    }


    /// <summary>PUT a JSON body, return the request status without parsing the response.</summary>
    protected async Task<ApiStatus> PutJsonNoBodyAsync<TReq>(string url, TReq body, CancellationToken ct = default)
    {
        var response = await Http.PutAsJsonAsync(url, body, ct).ConfigureAwait(false);
        return ApiStatus.From(response);
    }


    /// <summary>GET + JSON deserialize, with an optional query-string map. Returns <c>default</c> on 404 / null.</summary>
    protected Task<TRes?> GetJsonAsync<TRes>(string url, CancellationToken ct = default)
        => Http.GetFromJsonAsync<TRes>(url, JsonOptions.Web, ct);


    /// <summary>GET + JSON deserialize with a query-string dictionary appended to the URL.</summary>
    protected Task<TRes?> GetJsonAsync<TRes>(string url, IDictionary<string, string?> query, CancellationToken ct = default)
        => Http.GetFromJsonAsync<TRes>(QueryHelpers.AddQueryString(url, query), JsonOptions.Web, ct);


    /// <summary>DELETE returning the request status.</summary>
    protected async Task<ApiStatus> DeleteAsync(string url, CancellationToken ct = default)
    {
        var response = await Http.DeleteAsync(url, ct).ConfigureAwait(false);
        return ApiStatus.From(response);
    }


    /// <summary>POST with no body, deserialize the JSON response. Returns <c>default</c> on non-2xx.</summary>
    protected async Task<TRes?> PostNoBodyAsync<TRes>(string url, CancellationToken ct = default)
    {
        var response = await Http.PostAsync(url, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<TRes>(JsonOptions.Web, ct).ConfigureAwait(false);
    }


    /// <summary>
    /// POST (optionally with a JSON body) mapping the response into an <see cref="ApiOutcome{TValue,TError}"/>:
    /// 2xx → <c>Value</c>, 400 → typed <c>Error</c> body, 404 → <c>NotFound</c>. Use for endpoints that
    /// return a typed validation/problem payload the UI must surface.
    /// </summary>
    protected Task<ApiOutcome<TValue, TError>> PostForOutcomeAsync<TValue, TError>(
        string url, object? body = null, CancellationToken ct = default)
        where TValue : class
        where TError : class => SendForOutcomeAsync<TValue, TError>(HttpMethod.Post, url, body, ct);


    /// <summary>PUT a JSON body, mapping the response into an <see cref="ApiOutcome{TValue,TError}"/>
    /// (2xx → <c>Value</c>, 400 → typed <c>Error</c>, 404 → <c>NotFound</c>).</summary>
    protected async Task<ApiOutcome<TValue, TError>> PutForOutcomeAsync<TValue, TError>(
        string url, object body, CancellationToken ct = default)
        where TValue : class
        where TError : class => await SendForOutcomeAsync<TValue, TError>(
            HttpMethod.Put, url, body, ct).ConfigureAwait(false);


    protected async Task<ApiOutcome<TValue, TError>> SendForOutcomeAsync<TValue, TError>(
        HttpMethod method, string url, object? body, CancellationToken ct)
        where TValue : class
        where TError : class
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        // The caller shows a 400 itself from ErrorMessage: the global notifier must not add a second toast.
        request.Options.Set(ErrorNotificationHandler.CallerHandlesBadRequest, true);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return new(await response.Content.ReadFromJsonAsync<TValue>(JsonOptions.Web, ct).ConfigureAwait(false), null, false);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload))
                return new(null, null, false, response.StatusCode);
            // The reason is read from the body itself, so a refusal written by the error middleware
            // ({ message }), a ProblemDetails or plain text reads as well as the typed DTO.
            var message = ApiErrorText.Read(payload);
            try
            {
                return new(
                    null,
                    JsonSerializer.Deserialize<TError>(payload, JsonOptions.Web),
                    false,
                    response.StatusCode,
                    message);
            }
            catch (JsonException)
            {
                // Some legacy endpoints return a JSON string or plain text while the success path
                // expects a domain validation DTO: no typed error, but the reason is still carried.
                return new(null, null, false, response.StatusCode, message);
            }
        }

        return new(null, null, response.StatusCode == HttpStatusCode.NotFound, response.StatusCode);
    }


    /// <summary>POST with no body, returns the request status. Used for trigger/sync/promote-style endpoints.</summary>
    protected async Task<ApiStatus> PostNoBodyAsync(string url, CancellationToken ct = default)
    {
        var response = await Http.PostAsync(url, null, ct).ConfigureAwait(false);
        return ApiStatus.From(response);
    }


    /// <summary>
    /// POST a JSON body whose .NET type is only known at runtime (e.g. an anonymous type).
    /// Required because the strongly-typed <see cref="PostJsonAsync{TReq,TRes}"/> serializes against
    /// <c>TReq</c>; with <c>TReq = object</c> System.Text.Json would skip the runtime properties.
    /// </summary>
    protected async Task<TRes?> PostJsonAsync<TRes>(string url, object body, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(body, body.GetType());
        var response = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<TRes>(JsonOptions.Web, ct).ConfigureAwait(false);
    }


    /// <summary>POST a runtime-typed JSON body, return the request status without parsing the response.</summary>
    protected async Task<ApiStatus> PostJsonNoBodyAsync(string url, object body, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(body, body.GetType());
        var response = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        return ApiStatus.From(response);
    }


    protected static Dictionary<string, string?> PageQuery(
        int page, int pageSize, string? search, string? sortBy, bool sortDescending)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(search)) query["search"] = search;
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            query["sortBy"] = sortBy;
            query["sortDescending"] = sortDescending.ToString();
        }
        return query;
    }


    protected static Dictionary<string, string?> BuildPaginationQuery(
        int page, int pageSize, string? search, string? sortBy, bool sortDescending) => new()
        {
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString(),
            ["search"] = search,
            ["sortBy"] = sortBy,
            ["sortDescending"] = sortDescending.ToString().ToLowerInvariant()
        };


    protected static Dictionary<string, string?> PageQuery(int page, int pageSize, string? search = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString()
        };
        if (!string.IsNullOrWhiteSpace(search)) query["search"] = search;
        return query;
    }
}
