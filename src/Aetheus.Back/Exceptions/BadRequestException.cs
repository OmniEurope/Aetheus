// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Exceptions;

/// <summary>A 400. <see cref="Exception.Message"/> stays English (logs, audit, API clients);
/// <see cref="Problems"/>, when given, carries the same refusal as codes the front translates.</summary>
public sealed class BadRequestException(string message, IReadOnlyList<ProblemDto>? problems = null) : Exception(message)
{
    public IReadOnlyList<ProblemDto>? Problems { get; } = problems is { Count: > 0 } ? problems : null;
}
