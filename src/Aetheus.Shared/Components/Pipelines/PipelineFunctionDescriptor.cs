// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// What a <c>type: aetheus</c> function is allowed to touch. It decides the task kind, and through
/// it the agent capability required: a build function never reaches an agent that only holds the
/// deployment grant for it, and the other way round.
/// </summary>
public enum PipelineFunctionAccess
{
    /// <summary>Works on the checked-out workspace and publishes outputs; touches nothing deployed.
    /// Runs as <see cref="OperationKind.PipelineFunction"/>.</summary>
    Build,

    /// <summary>Acts on a deployed environment. Runs as
    /// <see cref="OperationKind.PipelineDeploymentFunction"/>.</summary>
    Deployment
}

/// <summary>One parameter a function accepts in the step's <c>with:</c> map.</summary>
/// <param name="Name">The <c>with:</c> key, in snake_case.</param>
/// <param name="Required">Whether the step is refused at save time when the key is absent.</param>
/// <param name="Description">What the value means, shown to whoever writes the step.</param>
public sealed record PipelineFunctionParameter(string Name, bool Required, string Description);

/// <summary>
/// The contract of one function: its name in <c>function:</c>, its access level, the parameters it
/// accepts and the run variables it always publishes. The outputs are fixed so that a later step can
/// reference them and the control plane can check that reference before the run starts.
/// </summary>
public sealed record PipelineFunctionDescriptor(
    string Name,
    PipelineFunctionAccess Access,
    string Description,
    IReadOnlyList<PipelineFunctionParameter> Parameters,
    IReadOnlyList<string> Outputs)
{
    /// <summary>
    /// The parameter whose value names one more run variable the function MAY publish, beside its fixed
    /// <see cref="Outputs"/>, or <c>null</c>. It exists for a value whose name belongs to the caller (the
    /// failed test names published beside a gate status, <c>&lt;GATE&gt;_FAILED_TESTS</c>), which no
    /// fixed name can carry. Unlike a fixed output it is optional, and a later step cannot reference it,
    /// because the control plane does not know its name before the run.
    /// </summary>
    public string? NamedOutputParameter { get; init; }

    /// <summary>The task kind this function is dispatched as.</summary>
    public OperationKind Operation => PipelineFunctionCatalog.OperationFor(Access);

    /// <summary>The declared parameter with that <c>with:</c> key, or <c>null</c>.</summary>
    public PipelineFunctionParameter? FindParameter(string name) =>
        Parameters.FirstOrDefault(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal));
}
