// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Standalone shell scripts cannot interpret control-plane variable or template syntax,
/// even when shell syntax checking accepts it. Pass values as named environment variables from the
/// pipeline and read ordinary shell variables in the script.</summary>
public class ShellScriptControlPlaneSyntaxAuditTests
{
    /// <summary>`$(NAME)` where NAME is a bare uppercase identifier. Deliberately narrow: a real
    /// command substitution such as `$(cat file)` or `$(sh helper.sh)` contains a space or a
    /// lowercase letter, so it never matches. The control-plane default form `$(NAME:-value)` is the
    /// same mistake - in a script it runs a command called `NAME:-value` - so it matches too.</summary>
    private static readonly Regex PipelineVariable = new(
        @"\$\([A-Z][A-Z0-9_]*(?::-[^()]*)?\)", RegexOptions.Compiled);

    [Theory]
    [InlineData("tag=\"$(IMAGE_TAG)\"", true)]
    [InlineData("tag=\"$(IMAGE_TAG:-latest)\"", true)]
    [InlineData("tag=\"$(IMAGE_TAG:-)\"", true)]
    [InlineData("now=\"$(date -u)\"", false)]
    [InlineData("tag=\"${IMAGE_TAG:-latest}\"", false)]
    public void PipelineVariablePattern_CatchesBothControlPlaneForms(string line, bool offending)
        => Assert.Equal(offending, PipelineVariable.IsMatch(line));

    private static readonly Regex TemplateExpression = new(
        @"\$\{\{", RegexOptions.Compiled);

    private static readonly string[] KnownLiveScripts =
    [
        "deploy/scripts/deploy.sh",
        "deploy/scripts/release-fast-deploy.sh",
        "deploy/scripts/install-agent-linux.sh",
    ];

    [Fact]
    public void NoDeliveryScript_ContainsControlPlaneSubstitution()
    {
        var scriptsDir = Path.Combine(RepositoryScan.Root, "deploy", "scripts");
        Assert.True(Directory.Exists(scriptsDir), $"Scripts dir not found: {scriptsDir}");

        var offenders = new List<string>();
        var scanned = new List<string>();
        foreach (var file in RepositoryScan.Enumerate(scriptsDir, "*.sh"))
        {
            var relative = Path.GetRelativePath(RepositoryScan.Root, file);
            scanned.Add(relative.Replace('\\', '/'));
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                // A header comment documents the trap on purpose and must stay readable.
                if (line.TrimStart().StartsWith('#')) continue;
                if (PipelineVariable.IsMatch(line))
                    offenders.Add($"{relative}:{lineNumber} (pipeline variable) {line.Trim()}");
                if (TemplateExpression.IsMatch(line))
                    offenders.Add($"{relative}:{lineNumber} (template expression) {line.Trim()}");
            }
        }

        // A path bug shows as known live scripts missing from the scan, whatever the number of scripts:
        // the manual deployment (README), the pipeline-run fast release, and the agent installer
        // (launch-linux.sh, Dockerfile.back).
        Assert.True(scanned.Count > 0, $"Scanner found no shell script under {scriptsDir} - likely a path bug.");
        foreach (var known in KnownLiveScripts)
            Assert.True(scanned.Contains(known), $"Scanner did not see {known} - likely a path bug.");
        Assert.True(
            offenders.Count == 0,
            "Control-plane substitution syntax only works inside a pipeline's shell: block. In a .sh "
            + "file $(NAME) runs a command called NAME and ${{ ... }} is a bad substitution that "
            + "aborts the script. Pass the value by name from the calling step (NAME=\"$(NAME)\" sh "
            + "script.sh) and read ${NAME} here. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
