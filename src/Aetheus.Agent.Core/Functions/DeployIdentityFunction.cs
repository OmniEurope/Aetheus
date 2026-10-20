// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>deploy-identity</c> function, port of <c>deploy/scripts/deploy-identity.sh</c>: derives the
/// deployment identity from the owning project and agent, so a copied pipeline cannot silently target
/// another installation. The script was sourced and left shell variables behind; the function
/// publishes them: <c>PROJECT_SLUG</c> (the project name lower-cased, each run of other characters a
/// single <c>-</c>, no leading or trailing <c>-</c>), <c>BACK_IMAGE_REPO</c> and
/// <c>FRONT_IMAGE_REPO</c> (<c>&lt;slug&gt;-back</c>, <c>&lt;slug&gt;-front</c>) and
/// <c>AGENT_HELPERS_DIR</c> (absolute, no traversal).
///
/// Differences, deliberate: <c>BUILD_PROJECTNAME</c> and <c>AGENT_HELPERS_DIR</c> are the
/// <c>project_name</c> and <c>helpers_dir</c> parameters, defaulting to the same variables. The script
/// validated the helper directory when sourced and named the images only when
/// <c>deploy_image_repos</c> was called; the function does both, in that order, so a project name with
/// no usable character fails it even where only the helper directory was wanted.
/// </summary>
public sealed class DeployIdentityFunction : IPipelineFunction
{
    /// <summary>The installer's historical helper directory, the only fallback for an agent that does
    /// not publish <c>AGENT_HELPERS_DIR</c>.</summary>
    internal const string DefaultHelpersDirectory = "/usr/local/lib/aetheus";

    public string Name => PipelineFunctionCatalog.DeployIdentity.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var helpers = FunctionShell.Input(context, "helpers_dir", "AGENT_HELPERS_DIR") ?? DefaultHelpersDirectory;
        if (helpers.Contains("..", StringComparison.Ordinal))
            return PipelineFunctionResult.Failure($"AGENT_HELPERS_DIR must not contain a traversal: {helpers}");
        if (!helpers.StartsWith('/'))
            return PipelineFunctionResult.Failure($"AGENT_HELPERS_DIR must be an absolute path: {helpers}");

        var slug = Slug(FunctionShell.Input(context, "project_name", "BUILD_PROJECTNAME") ?? string.Empty);
        if (slug.Length == 0)
            return PipelineFunctionResult.Failure("BUILD_PROJECTNAME is missing or has no usable character; the images cannot be named.");

        await context.Log($"Deployment identity: images {slug}-back and {slug}-front, agent helpers in {helpers}.").ConfigureAwait(false);
        context.SetOutput("PROJECT_SLUG", slug);
        context.SetOutput("BACK_IMAGE_REPO", slug + "-back");
        context.SetOutput("FRONT_IMAGE_REPO", slug + "-front");
        context.SetOutput("AGENT_HELPERS_DIR", helpers);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The script's <c>tr '[:upper:]' '[:lower:]' | sed -e 's/[^a-z0-9][^a-z0-9]*/-/g' -e
    /// 's/^-//' -e 's/-$//'</c>: ASCII letters lower-cased, each run of anything else one <c>-</c>,
    /// then one leading and one trailing <c>-</c> removed.</summary>
    internal static string Slug(string name)
    {
        var slug = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            var lowered = character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
            if (lowered is >= 'a' and <= 'z' or >= '0' and <= '9')
                slug.Append(lowered);
            else if (slug.Length == 0 || slug[^1] != '-')
                slug.Append('-');
        }
        var value = slug.ToString();
        if (value.StartsWith('-'))
            value = value[1..];
        if (value.EndsWith('-'))
            value = value[..^1];
        return value;
    }
}
