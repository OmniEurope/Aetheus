// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>render-env-sample</c> function: the first-deploy block of the production preparation program
/// (<c>prod-deploy-prepare.sh</c>) together with the renderer it ran, <c>render-env-sample.sh</c> (the
/// <c>PREPARE_PROGRAM</c> and <c>RENDER_ENV_SAMPLE_PROGRAM</c> heredocs of
/// <c>.pipeline/aetheus-deploy-prod.yaml</c>), and what the host-side proof
/// <c>verify-prod-env-sample.sh</c> pinned about it.
///
/// When the environment file already exists nothing is rendered and nothing is drawn: its secrets are
/// reused verbatim and never regenerated. Otherwise the versioned template (the keys, their order and
/// the non-secret values) is rendered by <see cref="EnvSampleRenderer"/> in memory, so a template that
/// is refused writes no file at all; each <c>#{SECRET_HEX_&lt;N&gt;}#</c> marker becomes N random bytes as
/// 2N lowercase hexadecimal characters, which is what <c>openssl rand -hex N</c> printed. The result is
/// written to <c>&lt;file&gt;.first-deploy.&lt;pid&gt;</c> with mode 600 from the first byte and linked to
/// the environment file without overwriting it: when another deployment created the file in the
/// meantime, that one is reused and this one is dropped.
///
/// Same log lines as the script. Differences, deliberate: the template is the required <c>sample</c>
/// parameter (relative to the workspace, the script had it fixed to <c>deploy/env/prod.env.sample</c>)
/// and the run values <c>public_api_url</c>, <c>public_app_url</c> and <c>app_version</c> default to
/// <c>PUBLIC_API_URL</c>, <c>PUBLIC_APP_URL</c> and <c>APP_VERSION</c>; the renderer's refusal is logged as
/// an error and the failure reason is the preparation's "Could not render" sentence; the random bytes
/// come from the operating system's cryptographic generator, so the host needs no <c>openssl</c>, and a
/// refused draw is worded for that source; a secret longer than 1024 bytes is refused, where the script
/// would have drawn it; the run values the template does not name are not required, where the
/// preparation required all three before rendering.
/// </summary>
public sealed class RenderEnvSampleFunction : IPipelineFunction
{
    private readonly Func<int, string> _hexSource;

    public RenderEnvSampleFunction()
        : this(bytes => RandomNumberGenerator.GetHexString(bytes * 2, lowercase: true))
    {
    }

    /// <summary>Tests stand a known draw in.</summary>
    internal RenderEnvSampleFunction(Func<int, string> hexSource) => _hexSource = hexSource;

    public string Name => PipelineFunctionCatalog.RenderEnvSample.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var output = ProductionEnvFiles.EnvFileInput(context);
        if (output is null)
            return PipelineFunctionResult.Failure("ENV_FILE is required");
        if (ProductionStatePaths.IsSymbolicLink(output))
            return PipelineFunctionResult.Failure("Production environment file must not be a symbolic link.");
        if (File.Exists(output))
        {
            await context.Log($">>> Reusing existing secrets at {output} (not regenerated).").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        await context.Log($">>> First deploy: generating persistent secrets at {output}").ConfigureAwait(false);
        var sample = SamplePath(context);
        var rendered = await RenderAsync(context, sample, cancellationToken).ConfigureAwait(false);
        if (rendered is null)
            return PipelineFunctionResult.Failure($"Could not render {sample}; no production environment was written.");
        return await PersistAsync(context, output, rendered, cancellationToken).ConfigureAwait(false);
    }

    private static string SamplePath(PipelineFunctionContext context)
    {
        var sample = context.Parameters["sample"];
        return Path.IsPathRooted(sample) || context.Workspace is null ? sample : Path.Combine(context.Workspace, sample);
    }

    /// <summary>The rendered environment, or null after logging why the template is refused.</summary>
    private async Task<string?> RenderAsync(PipelineFunctionContext context, string sample, CancellationToken cancellationToken)
    {
        if (!File.Exists(sample))
        {
            await context.Log($"FATAL: Environment template not found: {sample}", TaskLogLevel.Error).ConfigureAwait(false);
            return null;
        }

        var values = new EnvSampleValues(
            FunctionShell.Input(context, "public_api_url", "PUBLIC_API_URL"),
            FunctionShell.Input(context, "public_app_url", "PUBLIC_APP_URL"),
            FunctionShell.Input(context, "app_version", "APP_VERSION"));
        var (content, error) = EnvSampleRenderer.Render(
            sample, await File.ReadAllTextAsync(sample, cancellationToken).ConfigureAwait(false), values, _hexSource);
        if (error is not null)
            await context.Log($"FATAL: {error}", TaskLogLevel.Error).ConfigureAwait(false);
        return content;
    }

    /// <summary><c>ln "$ENV_FILE_TMP" "$ENV_FILE"</c>: the environment file appears whole, mode 600, or not
    /// at all, and an environment another deployment created first is never overwritten.</summary>
    private static async Task<PipelineFunctionResult> PersistAsync(
        PipelineFunctionContext context, string output, string content, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ProductionStatePaths.Dirname(output)))
            return PipelineFunctionResult.Failure("Could not persist production secrets.");

        var temporary = $"{output}.first-deploy.{Environment.ProcessId}";
        try
        {
            await using (var stream = ProductionEnvFiles.CreateOwnerOnly(temporary))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, output, overwrite: false);
        }
        catch (IOException) when (File.Exists(output))
        {
            await context.Log($">>> Another deployment created {output}; reusing it.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PipelineFunctionResult.Failure("Could not persist production secrets.");
        }
        finally
        {
            File.Delete(temporary);
        }
        return PipelineFunctionResult.Success();
    }
}
