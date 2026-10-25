// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Aetheus.Shared.Components.AiTasks;

/// <summary>Where and as whom a completion is sent. The key is never written into an outcome.</summary>
public sealed record AiProviderSettings(AiProvider Provider, Uri BaseUrl, string ApiKey);

/// <summary>A completion, or the reason there is none. <see cref="Warnings"/> lists what was degraded
/// on the way (the effort dropped after a refusal, retries).</summary>
public sealed record AiCompletionOutcome(
    AiCompletionResult? Result,
    string? Error,
    bool TimedOut,
    IReadOnlyList<string> Warnings,
    int Attempts)
{
    public bool Succeeded => Result is not null;
}

/// <summary>The model identifiers a provider listed, or the reason there are none.</summary>
public sealed record AiModelListOutcome(IReadOnlyList<string>? Models, string? Error)
{
    public bool Succeeded => Models is not null;
}

/// <summary>
/// Sends one completion through a provider adapter with plain <see cref="HttpClient"/>: no vendor SDK.
/// 429, 5xx and 529 (Anthropic "overloaded") are retried with exponential backoff, or the
/// provider's <c>Retry-After</c> when it gives one, at most <see cref="MaxAttempts"/> times and never
/// past the timeout, which bounds the whole exchange. A 400 that names the effort parameter is retried
/// once without it, with a warning: some models of an OpenAI-compatible vendor refuse it. Every other
/// status is an error worded for the person reading the run, with the provider's message and the API
/// key masked out of it.
/// </summary>
public sealed class AiProviderClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const int MaxAttempts = 4;

    /// <summary>The longest model identifier kept from a list, the size of a profile's model column.</summary>
    public const int MaxModelIdLength = 200;

    /// <summary>The most identifiers kept from one model list.</summary>
    public const int MaxListedModels = 500;

    private const int MaxErrorLength = 500;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<AiCompletionOutcome> CompleteAsync(
        AiProviderSettings settings, AiCompletionRequest request, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        var adapter = AiProviderCatalog.Adapter(settings.Provider);
        var warnings = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var sendEffort = AiEffortTokens.Of(request.Effort) is not null;
        var attempt = 0;
        try
        {
            while (true)
            {
                attempt++;
                var exchange = await SendAsync(adapter, settings, request, sendEffort, deadline.Token)
                    .ConfigureAwait(false);
                if (exchange.Status is { } status && (int)status is >= 200 and < 300)
                    return Parse(adapter, settings, exchange.Body, warnings, attempt);
                if (exchange.Status == HttpStatusCode.BadRequest && sendEffort && adapter.RejectsEffort(exchange.Body))
                {
                    sendEffort = false;
                    warnings.Add(
                        $"The model refused the reasoning effort '{AiEffortTokens.Of(request.Effort)}'; sent again without it.");
                    continue;
                }
                var problem = Describe(exchange, settings.ApiKey);
                if (!IsRetryable(exchange.Status) || attempt >= MaxAttempts)
                    return new AiCompletionOutcome(null, problem, false, warnings, attempt);
                var wait = exchange.RetryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                warnings.Add($"Attempt {attempt} failed ({problem}); retrying in {wait.TotalSeconds:0.#} s.");
                await _delay(wait, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiCompletionOutcome(
                null,
                $"The AI provider did not answer within {timeout.TotalSeconds:0} s.",
                true,
                warnings,
                attempt);
        }
    }

    /// <summary>
    /// Reads the model identifiers the key can use, in one attempt bounded by <paramref name="timeout"/>:
    /// the caller reads the list again later, so a failure is reported, not retried. The identifiers
    /// come back distinct and sorted; the error, like a completion error, has the key masked out.
    /// </summary>
    public async Task<AiModelListOutcome> ListModelsAsync(
        AiProviderSettings settings, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var adapter = AiProviderCatalog.Adapter(settings.Provider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        Exchange exchange;
        try
        {
            using var message = adapter.CreateModelsRequest(settings.BaseUrl, settings.ApiKey);
            exchange = await SendAsync(message, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiModelListOutcome(null, $"The AI provider did not answer within {timeout.TotalSeconds:0} s.");
        }
        if (exchange.Status is not { } status || (int)status is < 200 or >= 300)
            return new AiModelListOutcome(null, Describe(exchange, settings.ApiKey));
        try
        {
            var models = adapter.ParseModels(exchange.Body)
                .Where(id => id.Length <= MaxModelIdLength)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Take(MaxListedModels)
                .ToList();
            return models.Count == 0
                ? new AiModelListOutcome(null, "The AI provider listed no model usable by an AI task.")
                : new AiModelListOutcome(models, null);
        }
        catch (JsonException ex)
        {
            return new AiModelListOutcome(
                null, Mask($"The AI provider answered with an unreadable model list: {ex.Message}", settings.ApiKey));
        }
    }

    private async Task<Exchange> SendAsync(
        IAiProviderAdapter adapter, AiProviderSettings settings, AiCompletionRequest request,
        bool sendEffort, CancellationToken ct)
    {
        using var message = adapter.CreateRequest(settings.BaseUrl, settings.ApiKey, request, sendEffort);
        return await SendAsync(message, ct).ConfigureAwait(false);
    }

    private async Task<Exchange> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new Exchange(response.StatusCode, body, RetryAfter(response), null);
        }
        catch (HttpRequestException ex)
        {
            // Mandatory: a refused connection or a DNS failure is an answer to report (and retry), not a
            // crash of the task.
            return new Exchange(null, string.Empty, null, ex.Message);
        }
    }

    private static AiCompletionOutcome Parse(
        IAiProviderAdapter adapter, AiProviderSettings settings, string body, List<string> warnings, int attempt)
    {
        try
        {
            return new AiCompletionOutcome(adapter.ParseResponse(body), null, false, warnings, attempt);
        }
        catch (JsonException ex)
        {
            return new AiCompletionOutcome(
                null, Mask($"The AI provider answered with an unreadable body: {ex.Message}", settings.ApiKey),
                false, warnings, attempt);
        }
    }

    private static bool IsRetryable(HttpStatusCode? status) =>
        status is null
        || status == HttpStatusCode.TooManyRequests
        || (int)status.Value >= 500;

    private static string Describe(Exchange exchange, string apiKey)
    {
        if (exchange.Status is not { } status)
            return Mask($"The AI provider could not be reached: {exchange.TransportError}", apiKey);
        var detail = AiProviderJson.ErrorMessage(exchange.Body) ?? exchange.Body;
        if (detail.Length > MaxErrorLength) detail = detail[..MaxErrorLength] + "...";
        var code = (int)status;
        var meaning = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "the API key was refused",
            HttpStatusCode.NotFound => "endpoint or model not found (check the base URL and the model)",
            HttpStatusCode.BadRequest => "the request was refused",
            HttpStatusCode.TooManyRequests => "rate limited",
            _ when code == 529 => "the provider is overloaded",
            _ when code >= 500 => "provider error",
            _ => "unexpected answer"
        };
        var text = string.IsNullOrWhiteSpace(detail)
            ? $"AI provider HTTP {code}: {meaning}."
            : $"AI provider HTTP {code}: {meaning}. {detail.Trim()}";
        return Mask(text, apiKey);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta;
        if (wait is null
            && response.Headers.TryGetValues("retry-after", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            wait = TimeSpan.FromSeconds(seconds);
        if (wait is null) return null;
        return wait.Value < TimeSpan.Zero ? TimeSpan.Zero : wait.Value > MaxRetryAfter ? MaxRetryAfter : wait.Value;
    }

    /// <summary>Replaces the API key anywhere in a text bound for a log, a report or a person.</summary>
    public static string Mask(string text, string? apiKey) =>
        string.IsNullOrEmpty(apiKey) || apiKey.Length < 4
            ? text
            : text.Replace(apiKey, "***", StringComparison.Ordinal);

    private sealed record Exchange(HttpStatusCode? Status, string Body, TimeSpan? RetryAfter, string? TransportError);
}
