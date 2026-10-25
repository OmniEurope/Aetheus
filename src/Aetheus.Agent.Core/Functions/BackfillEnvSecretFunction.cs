// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>backfill-env-secret</c> function, port of the two key back-fill blocks of the production
/// preparation program (<c>prod-deploy-prepare.sh</c>, the <c>PREPARE_PROGRAM</c> heredoc of
/// <c>.pipeline/aetheus-deploy-prod.yaml</c>): <c>DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY</c> and the
/// <c>GIT_RUN_TOKEN_KEY</c> of R-464. The key is drawn once per environment, on the first preparation
/// that finds it absent, then reused verbatim: a value already present is never replaced, and drawing
/// it rotates nothing else.
///
/// When the environment file has no <c>KEY=value</c> line (<c>grep -q "^KEY=.\+"</c>) the function draws
/// 32 random bytes as 64 lowercase hexadecimal characters, refuses a draw of another shape before it
/// touches anything, keeps a dated copy <c>&lt;file&gt;-YYYYMMDDTHHMMSSZ</c> next to the file (never
/// overwritten, mode 600), and writes every other line unchanged plus <c>KEY=value</c> through a mode 600
/// temporary copy moved over the file. A line that is exactly <c>KEY=</c> counts as absent and is
/// dropped, not duplicated.
///
/// Differences, deliberate: the key is the required <c>key</c> parameter (the script had one block per
/// key) and the file the <c>env_file</c> parameter, defaulting to <c>ENV_FILE</c>; the state directory is
/// the file's own directory, which the preparation already required to be the declared one. The random
/// bytes come from the operating system's cryptographic generator instead of <c>openssl rand -hex 32</c>,
/// so the host needs no <c>openssl</c>, and a refused draw is worded for that source. The
/// <c>DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY</c> block never dropped an empty <c>KEY=</c> line and appended a
/// second one; it is now dropped like the run token's. A file whose last line has no newline gets the new
/// line on a line of its own (the script glued it to the last line). A symbolic-link environment file
/// is refused here as well, where the preparation had already refused it.
/// </summary>
public sealed partial class BackfillEnvSecretFunction : IPipelineFunction
{
    private const int SecretBytes = 32;

    [GeneratedRegex("^[A-Z][A-Z0-9_]*\\z")]
    private static partial Regex EnvironmentKey();

    private readonly TimeProvider _time;
    private readonly Func<int, string> _hexSource;

    public BackfillEnvSecretFunction()
        : this(TimeProvider.System, bytes => RandomNumberGenerator.GetHexString(bytes * 2, lowercase: true))
    {
    }

    /// <summary>Tests stand a clock and a known draw in.</summary>
    internal BackfillEnvSecretFunction(TimeProvider time, Func<int, string> hexSource)
    {
        _time = time;
        _hexSource = hexSource;
    }

    public string Name => PipelineFunctionCatalog.BackfillEnvSecret.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var key = context.Parameters["key"];
        if (!EnvironmentKey().IsMatch(key))
            return PipelineFunctionResult.Failure($"Not an environment key: '{key}'");
        var envFile = ProductionEnvFiles.EnvFileInput(context);
        if (envFile is null)
            return PipelineFunctionResult.Failure("ENV_FILE is required");
        if (ProductionStatePaths.IsSymbolicLink(envFile))
            return PipelineFunctionResult.Failure("Production environment file must not be a symbolic link.");
        if (!File.Exists(envFile))
            return PipelineFunctionResult.Failure($"{envFile} is missing.");

        var lines = ProductionEnvFiles.Lines(await File.ReadAllTextAsync(envFile, cancellationToken).ConfigureAwait(false));
        if (ProductionEnvFiles.HasValue(lines, key))
        {
            await context.Log($">>> {key} is already set in {envFile}; left unchanged.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        await context.Log($">>> Back-filling {key} in {envFile}").ConfigureAwait(false);
        var drawn = _hexSource(SecretBytes);
        if (DrawRefusal(key, drawn) is { } refusal)
            return PipelineFunctionResult.Failure(refusal);

        ProductionEnvFiles.EnsureDatedCopy(envFile, ProductionEnvFiles.DatedCopyPath(envFile, _time));
        lines.RemoveAll(line => line == key + "=");
        ProductionEnvFiles.Append(lines, $"{key}={drawn}");
        await ProductionEnvFiles.ReplaceAsync(
            envFile, $"{envFile}.backfill.{Environment.ProcessId}", ProductionEnvFiles.Join(lines), cancellationToken).ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The refusal of a draw that is not 32 bytes of lowercase hexadecimal, in the script's
    /// order (alphabet first, then length), or null.</summary>
    private static string? DrawRefusal(string key, string drawn)
    {
        if (drawn.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return $"The random source did not draw a hexadecimal {key}.";
        return drawn.Length == SecretBytes * 2 ? null : $"The random source did not draw a 32-byte {key}.";
    }
}
