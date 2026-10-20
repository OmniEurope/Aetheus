// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// A function task checked against the catalog before anything runs. The control plane validates the
/// same rules at save time; the agent checks them again because the task is the boundary it trusts, and
/// a function run on parameters its catalog entry does not describe would report on the wrong input.
/// </summary>
public sealed record PipelineFunctionRequest(
    PipelineFunctionDescriptor Function,
    string? Workspace,
    IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>The run variable holding the checked-out workspace.</summary>
    public const string WorkspaceVariable = "WORKSPACE";

    /// <summary>Checks the task against <paramref name="findFunction"/>, which is
    /// <see cref="PipelineFunctionCatalog.Find"/> outside tests.</summary>
    public static bool TryCreate(
        Func<string?, PipelineFunctionDescriptor?> findFunction,
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        out PipelineFunctionRequest? request,
        out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(findFunction);
        ArgumentNullException.ThrowIfNull(envVars);
        request = null;
        IReadOnlyDictionary<string, string>? parameters = null;
        string? workspace = null;
        rejection = ResolveFunction(findFunction, kind, target, out var function)
            ?? ResolveParameters(function!, envVars, out parameters)
            ?? ResolveWorkspace(function!, envVars, out workspace);
        if (rejection is not null)
            return false;
        request = new PipelineFunctionRequest(function!, workspace, parameters!);
        return true;
    }

    private static string? ResolveFunction(
        Func<string?, PipelineFunctionDescriptor?> findFunction, OperationKind kind, string target,
        out PipelineFunctionDescriptor? function)
    {
        function = findFunction(target);
        if (function is null)
            return $"'{target}' is not a function of the Aetheus pipeline catalog.";
        if (function.Operation != kind)
            return $"Function '{function.Name}' has {function.Access} access and runs as {function.Operation}, not as {kind}.";
        return null;
    }

    private static string? ResolveParameters(
        PipelineFunctionDescriptor function,
        IReadOnlyDictionary<string, string> envVars,
        out IReadOnlyDictionary<string, string>? parameters)
    {
        parameters = null;
        if (!PipelineFunctionVariables.TryParseParameters(
                envVars.GetValueOrDefault(PipelineFunctionVariables.ParametersJson), out var decoded))
            return $"{PipelineFunctionVariables.ParametersJson} is not a JSON object of string values.";
        var unknown = decoded.Keys.FirstOrDefault(key => function.FindParameter(key) is null);
        if (unknown is not null)
            return $"Function '{function.Name}' has no parameter '{unknown}'.";
        var missing = function.Parameters.FirstOrDefault(parameter =>
            parameter.Required && string.IsNullOrWhiteSpace(decoded.GetValueOrDefault(parameter.Name)));
        if (missing is not null)
            return $"Function '{function.Name}' requires the parameter '{missing.Name}'.";
        parameters = decoded;
        return null;
    }

    private static string? ResolveWorkspace(
        PipelineFunctionDescriptor function,
        IReadOnlyDictionary<string, string> envVars,
        out string? workspace)
    {
        workspace = envVars.GetValueOrDefault(WorkspaceVariable);
        if (string.IsNullOrWhiteSpace(workspace))
            workspace = null;
        return workspace is null && function.Access == PipelineFunctionAccess.Build
            ? $"{WorkspaceVariable} is required: build function '{function.Name}' works on the checked-out workspace."
            : null;
    }
}
