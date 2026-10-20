// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AiTasks;

/// <summary>
/// Recette R3-7 (decision 4 of 2026-10-13): keeps, from an OpenAI or OpenAI-compatible
/// <c>GET /v1/models</c> list, only the models an AI task can talk to through Chat Completions. The
/// rule is a deny list: an identifier is cut into its words (on <c>-</c>, <c>_</c>, <c>.</c>,
/// <c>:</c> and <c>/</c>, case ignored) and dropped when one word starts with a marker of
/// <see cref="DeniedWordPrefixes"/> (embeddings, images, audio, speech, transcription, moderation,
/// realtime, video, reranking, legacy completion bases, computer use). Everything else is kept, so a
/// chat model of another vendor serving the same API (Mistral, Llama, Qwen...) stays listed. The
/// Anthropic list holds only chat models and is not filtered.
/// </summary>
public static class AiChatModelFilter
{
    /// <summary>The start of a word that marks a model which is not a chat model.</summary>
    public static readonly IReadOnlyList<string> DeniedWordPrefixes =
    [
        "embed",      // text-embedding-3-small, nomic-embed-text, embeddings
        "dall",       // dall-e-3
        "image",      // gpt-image-1, chatgpt-image-latest
        "tts",        // tts-1, gpt-4o-mini-tts
        "whisper",    // whisper-1
        "audio",      // gpt-4o-audio-preview, gpt-audio
        "speech",     // speech models
        "transcribe", // gpt-4o-transcribe
        "moderation", // omni-moderation-latest, text-moderation-stable
        "realtime",   // gpt-4o-realtime-preview, gpt-realtime
        "sora",       // video
        "rerank",     // bge-reranker, rerank-v3
        "babbage",    // babbage-002, legacy completions only
        "davinci",    // davinci-002, legacy completions only
        "computer"    // computer-use-preview, Responses API only
    ];

    private static readonly char[] Separators = ['-', '_', '.', ':', '/'];

    /// <summary>True when <paramref name="modelId"/> names a chat model under the rule above.</summary>
    public static bool IsChatModel(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        foreach (var word in modelId.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var prefix in DeniedWordPrefixes)
            {
                if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        return true;
    }
}
