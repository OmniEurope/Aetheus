// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;

namespace Aetheus.Front.Components.Shared;

/// <summary>Which API attempts the resilience pipeline of the API client may send again.</summary>
internal static class ApiRetryPolicy
{
    /// <summary>Marks a POST or PATCH whose body also carries a key the server deduplicates on (the manual
    /// run trigger sends the same value as <c>idempotencyKey</c>), so the resilience pipeline may send it again.
    /// The back reads the key from the body only. This is a request option, never a header: an extra header would
    /// have to be allowed by the back's CORS policy (front and API are different origins) or the preflight fails.</summary>
    internal static readonly HttpRequestOptionsKey<string> IdempotencyKeyOption = new("Aetheus.IdempotencyKey");

    private static bool IsNonIdempotent(HttpMethod method) => method == HttpMethod.Post || method == HttpMethod.Patch;

    /// <summary>
    /// Retries 5xx answers and transient network errors, never 429 (each retry eats more rate-limit budget
    /// and spirals under load) and never a token renewal. A refresh is not idempotent: the server rotates
    /// the token it receives, so when the answer is lost after the rotation (a 502 from the proxy, a reset
    /// connection) a second send of the same token gets the grace answer, an access token without a
    /// refresh token. The tab then keeps the consumed token as its only one and, five minutes later, the
    /// next renewal reads as a replay and ends the session (recette R1-88).
    /// </summary>
    public static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, HttpRequestMessage? request)
    {
        if (request?.Headers.Contains(AuthDelegatingHandler.RenewalMarkerHeader) == true)
            return false;
        // F-FRONT-003: a POST or PATCH answered 502 may already have been applied (a rerun inserts its run
        // before the proxy loses the answer), so sending it again duplicates the effect. Only a request that
        // carries an idempotency key, which the server replays instead of repeating, is sent again.
        if (request is not null && IsNonIdempotent(request.Method) && !request.Options.TryGetValue(IdempotencyKeyOption, out _))
            return false;
        return outcome.Exception is HttpRequestException or TimeoutException
            || (outcome.Result is { IsSuccessStatusCode: false } response && (int)response.StatusCode >= 500);
    }

    /// <summary>The <see cref="HttpRetryStrategyOptions.ShouldHandle"/> predicate built on <see cref="ShouldRetry"/>.</summary>
    public static ValueTask<bool> ShouldHandle(RetryPredicateArguments<HttpResponseMessage> args) =>
        ValueTask.FromResult(ShouldRetry(args.Outcome, args.Context.GetRequestMessage()));
}
