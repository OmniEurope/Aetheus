// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>What a function reports when it returns normally. A failure carries a plain cause that the
/// step shows as its failure reason.</summary>
public sealed record PipelineFunctionResult
{
    private PipelineFunctionResult(bool succeeded, string? failureReason)
    {
        Succeeded = succeeded;
        FailureReason = failureReason;
    }

    public bool Succeeded { get; }

    public string? FailureReason { get; }

    public static PipelineFunctionResult Success() => new(true, null);

    public static PipelineFunctionResult Failure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PipelineFunctionResult(false, reason);
    }
}
