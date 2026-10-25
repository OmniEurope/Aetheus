// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AiTasks;

/// <summary>The wire words of a reasoning effort, shared by every provider adapter and by whoever stores or
/// reads an effort as text.</summary>
public static class AiEffortTokens
{
    /// <summary>The wire value of an effort, the same three words for both providers; null for
    /// <see cref="AiReasoningEffort.Default"/>, which sends nothing.</summary>
    public static string? Of(AiReasoningEffort effort) => effort switch
    {
        AiReasoningEffort.Low => "low",
        AiReasoningEffort.Medium => "medium",
        AiReasoningEffort.High => "high",
        _ => null
    };

    /// <summary>The effort a stored word stands for; <see cref="AiReasoningEffort.Default"/> for any other text.</summary>
    public static AiReasoningEffort Parse(string? token) => token switch
    {
        "low" => AiReasoningEffort.Low,
        "medium" => AiReasoningEffort.Medium,
        "high" => AiReasoningEffort.High,
        _ => AiReasoningEffort.Default
    };
}
