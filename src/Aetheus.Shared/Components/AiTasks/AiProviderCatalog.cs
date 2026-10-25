// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AiTasks;

/// <summary>
/// The stored tokens, default endpoints and adapters of the AI providers. The database keeps the
/// provider and the effort as plain strings so a row written by an older version (a CLI profile has
/// no provider) reads as "to reconfigure" instead of failing to load.
/// </summary>
public static class AiProviderCatalog
{
    public const string AnthropicToken = "anthropic";
    public const string OpenAiToken = "openai";

    public static string Token(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => AnthropicToken,
        AiProvider.OpenAi => OpenAiToken,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    public static bool TryParse(string? token, out AiProvider provider)
    {
        provider = default;
        switch (token)
        {
            case AnthropicToken:
                provider = AiProvider.Anthropic;
                return true;
            case OpenAiToken:
                provider = AiProvider.OpenAi;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The base URL used when a profile leaves it empty.</summary>
    public static string DefaultBaseUrl(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => "https://api.anthropic.com",
        AiProvider.OpenAi => "https://api.openai.com/v1",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    /// <summary>The model identifiers the profile dialog offers for a provider, the first being the default.
    /// A list kept in the application, not the provider's own: a model that is missing is typed as an
    /// identifier in the dialog, so the list never blocks a profile.</summary>
    public static IReadOnlyList<string> KnownModels(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => ["claude-sonnet-5-5", "claude-opus-5-5"],
        AiProvider.OpenAi => ["gpt-5.5"],
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    /// <summary>An absolute https URL with no credentials, query or fragment. Plain http is refused: the
    /// request carries the API key.</summary>
    public static bool IsValidBaseUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        uri = parsed;
        return true;
    }

    public static IAiProviderAdapter Adapter(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => AnthropicMessagesAdapter.Instance,
        AiProvider.OpenAi => OpenAiChatCompletionsAdapter.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}
