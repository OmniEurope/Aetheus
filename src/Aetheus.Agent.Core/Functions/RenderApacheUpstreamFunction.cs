// SPDX-License-Identifier: EUPL-1.2
using System.Numerics;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>render-apache-upstream</c> function, port of <c>render-apache-upstream.sh</c> (the
/// <c>RENDER_APACHE_UPSTREAM_PROGRAM</c> heredoc of the production, nightly, demo and fast release
/// pipelines): renders the versioned blue-green upstream template, <c>#{...}#</c> tokens replaced by
/// validated values only, and writes it where the environment says it belongs.
///
/// Two modes, as the script's. With <c>define_only</c> only the Define name is resolved, into a copy
/// local to the run: the copy must still carry the front and back port placeholders (a copy with
/// nothing left to substitute would make the switch reload the configuration already in place and
/// report a cutover that never happened) and no Define placeholder. Otherwise the whole template is
/// rendered: the destination must be the environment's <c>UPSTREAM_CONF</c> (a file in a
/// <c>sites-available</c> directory, no traversal), the colour is <c>blue</c> or <c>green</c>, the ports
/// are digits within 1 to 65535, and no token may be left. The file is written through a temporary copy
/// moved into place, so a refusal leaves the destination as it was.
///
/// Same refusals and sentences as the script. Differences, deliberate: the parameters replace the
/// positional arguments (<c>template</c>, <c>output</c>, <c>front_port</c>, <c>back_port</c>,
/// <c>active_color</c>) and the <c>UPSTREAM_DEFINE</c> and <c>UPSTREAM_CONF</c> variables
/// (<c>define</c>, <c>upstream_conf</c>, defaulting to them); a relative <c>template</c> or
/// <c>output</c> is read from the workspace; the exit code 2 of every refusal is the function's
/// failure. The template path is still the Aetheus one, <c>.pipeline/configs/apache/aetheus-upstream.conf</c>.
/// </summary>
public sealed partial class RenderApacheUpstreamFunction : IPipelineFunction
{
    private const string TemplateSuffix = "/.pipeline/configs/apache/aetheus-upstream.conf";
    private const int LastPort = 65535;

    [GeneratedRegex("/sites-available/.", RegexOptions.Singleline)]
    private static partial Regex SitesAvailableFile();

    [GeneratedRegex("^[0-9]+\\z")]
    private static partial Regex Digits();

    public string Name => PipelineFunctionCatalog.RenderApacheUpstream.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var template = context.Parameters["template"];
        var output = context.Parameters["output"];
        var define = FunctionShell.Input(context, "define", "UPSTREAM_DEFINE") ?? string.Empty;
        var defineOnly = string.Equals(context.Parameters.GetValueOrDefault("define_only"), "true", StringComparison.Ordinal);

        var templatePath = Resolve(context, template);
        var refusal = TemplateRefusal(template, templatePath, define);
        if (refusal is not null)
            return PipelineFunctionResult.Failure(refusal);

        var text = (await File.ReadAllTextAsync(templatePath, cancellationToken).ConfigureAwait(false))
            .Replace("#{UPSTREAM_DEFINE}#", define, StringComparison.Ordinal);
        var rendered = defineOnly ? DefineOnly(text) : Full(context, output, text);
        if (rendered.Error is not null)
            return PipelineFunctionResult.Failure(rendered.Error);

        var outputPath = Resolve(context, output);
        await WriteAsync(outputPath, rendered.Content!, cancellationToken).ConfigureAwait(false);
        await context.Log($">>> Rendered the Apache upstream {(defineOnly ? "Define" : "configuration")} to {outputPath}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>A relative path is read from the workspace.</summary>
    private static string Resolve(PipelineFunctionContext context, string path) =>
        Path.IsPathRooted(path) || context.Workspace is null ? path : Path.Combine(context.Workspace, path);

    private static string? TemplateRefusal(string given, string resolved, string define)
    {
        if (!resolved.Replace('\\', '/').EndsWith(TemplateSuffix, StringComparison.Ordinal))
            return $"Unexpected Apache upstream template: {given}";
        if (define.Length == 0 || define[0] is not (>= 'A' and <= 'Z'))
            return $"UPSTREAM_DEFINE must start with an upper-case letter: '{define}'";
        if (define.Any(character => character is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            return $"UPSTREAM_DEFINE must be upper case, digits and underscores: '{define}'";
        return File.Exists(resolved) ? null : $"Apache upstream template not found: {given}";
    }

    /// <summary>The copy the switch will render: only the Define name resolved, everything else left to
    /// the switch.</summary>
    private static (string? Content, string? Error) DefineOnly(string text)
    {
        if (!text.Contains("#{FRONT_PORT}#", StringComparison.Ordinal))
            return (null, "The upstream template lost its front-port placeholder.");
        if (!text.Contains("#{BACK_PORT}#", StringComparison.Ordinal))
            return (null, "The upstream template lost its back-port placeholder.");
        return text.Contains("#{UPSTREAM_DEFINE}#", StringComparison.Ordinal)
            ? (null, "The Define name was not resolved.")
            : (text, null);
    }

    private static (string? Content, string? Error) Full(PipelineFunctionContext context, string output, string text)
    {
        var expected = FunctionShell.Input(context, "upstream_conf", "UPSTREAM_CONF") ?? string.Empty;
        var frontPort = context.Parameters.GetValueOrDefault("front_port") ?? string.Empty;
        var backPort = context.Parameters.GetValueOrDefault("back_port") ?? string.Empty;
        var color = context.Parameters.GetValueOrDefault("active_color") ?? string.Empty;
        var refusal = DestinationRefusal(output, expected) ?? ValueRefusal(frontPort, backPort, color);
        if (refusal is not null)
            return (null, refusal);

        var rendered = text
            .Replace("#{FRONT_PORT}#", frontPort, StringComparison.Ordinal)
            .Replace("#{BACK_PORT}#", backPort, StringComparison.Ordinal)
            .Replace("#{ACTIVE_COLOR}#", color, StringComparison.Ordinal);
        return rendered.Contains("#{", StringComparison.Ordinal)
            ? (null, "Apache upstream template contains an unresolved token.")
            : (rendered, null);
    }

    private static string? DestinationRefusal(string output, string expected)
    {
        if (!SitesAvailableFile().IsMatch(expected))
            return $"UPSTREAM_CONF must name a file in a sites-available directory: '{expected}'";
        if (expected.Contains("..", StringComparison.Ordinal))
            return $"UPSTREAM_CONF must not contain a traversal: {expected}";
        return string.Equals(output, expected, StringComparison.Ordinal)
            ? null
            : $"Unexpected Apache upstream destination: {output} (expected {expected})";
    }

    private static string? ValueRefusal(string frontPort, string backPort, string color)
    {
        if (color is not ("blue" or "green"))
            return $"Invalid active colour: {color}";
        if (!Digits().IsMatch(frontPort))
            return $"Invalid front port: {frontPort}";
        if (!Digits().IsMatch(backPort))
            return $"Invalid back port: {backPort}";
        if (!InRange(frontPort))
            return "Front port is out of range.";
        return InRange(backPort) ? null : "Back port is out of range.";
    }

    private static bool InRange(string digits) =>
        BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) is var port && port >= 1 && port <= LastPort;

    private static async Task WriteAsync(string output, string content, CancellationToken cancellationToken)
    {
        var temporary = $"{output}.tmp.{Environment.ProcessId}";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
