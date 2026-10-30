// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The variables a step runs with once its environment file is rendered, or why it could not be.</summary>
public sealed record EnvFilePreparation(
    Dictionary<string, string>? Variables, HashSet<string>? SecretKeys, string? Error,
    string FailureCode = TaskFailureCodes.InfrastructureMismatch);

/// <summary>
/// Renders the environment sample a step names in <c>env_template</c> (ADR-057) from the run's resolved
/// variables, the vaults and the libraries, and hands the result to the step as base64 in
/// <see cref="VariableName"/>. This is the one place a vault secret reaches a deployment without the step's
/// shell naming it: only the entries the versioned sample asks for are written, only into that file, and the
/// file's variable is itself a secret, so it reaches only a step whose shell mentions it
/// (<see cref="PipelineRunHelpers.ScopeStepEnvironment"/>) and is masked from the logs.
/// </summary>
public interface IPipelineEnvFilePreparer
{
    Task<EnvFilePreparation> PrepareAsync(
        int runId, PipelineStepDefinition stepDef, Dictionary<string, string> variables,
        HashSet<string> secretKeys, CancellationToken ct);
}

public sealed class PipelineEnvFilePreparer(
    IPipelineRepository repo,
    IProjectConfigRepositoryService configRepository,
    ISecretMaskingService secretMasking) : IPipelineEnvFilePreparer
{
    /// <summary>The variable that carries the rendered file, base64 encoded, to the step.</summary>
    internal const string VariableName = "AETHEUS_ENV_FILE_B64";

    public async Task<EnvFilePreparation> PrepareAsync(
        int runId, PipelineStepDefinition stepDef, Dictionary<string, string> variables,
        HashSet<string> secretKeys, CancellationToken ct)
    {
        var path = PipelineRunHelpers.SubstituteVariables(stepDef.EnvTemplate ?? string.Empty, variables).Trim();
        if (!PipelineConfigTemplateRenderer.IsSafeConfigPath(path))
            return Failed($"env_template '{path}' is not a safe path below .pipeline/configs/.");

        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        if (run?.Pipeline?.ProjectId is not { } projectId)
            return Failed("env_template needs a pipeline that belongs to a project.");

        var template = await configRepository.ReadAsync(
            projectId, path, PipelineRunService.ResolveDefinitionCommit(run), run.Pipeline.SourceRepositoryId, ct)
            .ConfigureAwait(false);
        if (template is null)
            return Failed(
                $"env_template '{path}' was found neither in the project's configuration repository nor in its "
                + "source: prepare the production deployment from the pipeline setup wizard, which creates it.");

        string rendered;
        try
        {
            rendered = PipelineEnvTemplateRenderer.Render(template, variables);
        }
        catch (BadRequestException exception)
        {
            // Mandatory: the renderer refuses an undefined or empty value, and that refusal is the step's failure.
            // R-68: it is a configuration to complete, not a server that does not match the step.
            return new EnvFilePreparation(
                null, null, $"env_template '{path}': {exception.Message}", TaskFailureCodes.ConfigurationIncomplete);
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(rendered));
        secretMasking.RegisterRuntimeSecret(runId, encoded);
        return new EnvFilePreparation(
            new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase) { [VariableName] = encoded },
            new HashSet<string>(secretKeys, StringComparer.OrdinalIgnoreCase) { VariableName },
            null);
    }

    private static EnvFilePreparation Failed(string error) => new(null, null, error);
}
