// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AiTasks;

/// <summary>
/// The HTTP API an AI runner profile calls (ADR-023, amendment 2026-10-11). Adding a provider is one
/// <see cref="IAiProviderAdapter"/> and one value here; nothing else in the run path names a vendor.
/// </summary>
public enum AiProvider
{
    /// <summary>Anthropic Messages API (<c>POST {base}/v1/messages</c>).</summary>
    Anthropic = 1,

    /// <summary>OpenAI Chat Completions API (<c>POST {base}/chat/completions</c>), and any vendor that
    /// exposes the same API under its own base URL.</summary>
    OpenAi = 2
}

/// <summary>How much the model may reason. <see cref="Default"/> sends nothing and leaves the
/// provider's own default.</summary>
public enum AiReasoningEffort
{
    Default = 0,
    Low = 1,
    Medium = 2,
    High = 3
}
