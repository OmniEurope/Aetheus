// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;

namespace Aetheus.Front.Components.Shared;

/// <summary>Which API attempts the resilience pipeline of the API client may send again.</summary>
internal static class ApiRetryPolicy
{
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
        return outcome.Exception is HttpRequestException or TimeoutException
            || (outcome.Result is { IsSuccessStatusCode: false } response && (int)response.StatusCode >= 500);
    }

    /// <summary>The <see cref="HttpRetryStrategyOptions.ShouldHandle"/> predicate built on <see cref="ShouldRetry"/>.</summary>
    public static ValueTask<bool> ShouldHandle(RetryPredicateArguments<HttpResponseMessage> args) =>
        ValueTask.FromResult(ShouldRetry(args.Outcome, args.Context.GetRequestMessage()));
}
