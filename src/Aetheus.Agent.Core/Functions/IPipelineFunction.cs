// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The agent-side implementation of one <see cref="PipelineFunctionCatalog"/> entry (a
/// <c>type: aetheus</c> step). <see cref="Name"/> must equal the catalog name: the executor routes by
/// it, and a catalog entry with no implementation registered fails as <c>function-not-implemented</c>.
/// </summary>
public interface IPipelineFunction
{
    /// <summary>The catalog name this implementation answers to, case-sensitive.</summary>
    string Name { get; }

    /// <summary>Runs the function. Outputs are published through
    /// <see cref="PipelineFunctionContext.SetOutput"/>; a returned failure, or a thrown exception, fails
    /// the step as <c>function-error</c>. The token fires on timeout and on external cancellation, and
    /// the implementation must honour it.</summary>
    Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken);
}
