// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Shared;

public sealed record ApiError
{
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public Dictionary<string, string[]>? Errors { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>A machine-readable reason, when an endpoint defines them (e.g. <c>RefreshRejectionCodes</c>).</summary>
    public string? Code { get; init; }

    /// <summary>The refusal as codes and parameters, when the endpoint states one (a refused pipeline
    /// launch). <see cref="Message"/> keeps the same refusal in English.</summary>
    public List<ProblemDto>? Problems { get; init; }
}
