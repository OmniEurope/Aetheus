// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Host and environment values come from configuration or run-derived names rather than
/// deployment literals. Comment lines are excluded because they do not supply executable
/// values.</summary>
public sealed partial class HostLiteralAuditTests
{
    private static readonly string Root = RepositoryScan.Root;

    /// <summary>Files that legitimately carry such literals, each for a stated reason.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["deploy/scripts/install-agent-linux.sh"] =
            "the installer: its defaults and usage examples are the host layout it creates",
        ["deploy/scripts/verify-production-env-preservation.sh"] =
            "an installer test harness that reproduces the installer's layout in a temporary root",
        ["deploy/scripts/deploy.sh"] =
            "the manual deployment outside any pipeline (README), whose base ports are its documented defaults",
    };

    [GeneratedRegex(@"sonytumen|/var/lib/aetheus|/var/www/aetheus|/etc/apache2|\baetheus-(prod|demo|production|nightly)\b|\b100[23]\d\b")]
    private static partial Regex HostLiteral();

    [GeneratedRegex(@"^\s*(#|//)")]
    private static partial Regex CommentLine();

    [Theory]
    [InlineData("  STATE_DIR: \"/var/lib/aetheus-demo\"")]
    [InlineData("sudo -n /etc/apache2/x")]
    [InlineData("ServerName demo.app.aetheus.sonytumen.com")]
    [InlineData("  PORT_FRONT_BLUE: \"10025\"")]
    [InlineData("docker volume inspect aetheus-demo-db-data")]
    public void TheLiteralPattern_CatchesEveryShapeItWasWrittenFor(string line) =>
        Assert.Matches(HostLiteral(), line);

    [Theory]
    [InlineData("artifact: aetheus-back.tar.gz")]
    [InlineData("  COMPOSE_PROJECT: \"$(COMPOSE_PROJECT)\"")]
    [InlineData("timeout_seconds: 1002")]
    public void TheLiteralPattern_LeavesDerivedNamesAlone(string line) =>
        Assert.DoesNotMatch(HostLiteral(), line);

    /// <summary>Shell and Node scripts under deploy/scripts except the tests, and every pipeline definition and configuration template under .pipeline.</summary>
    private static IEnumerable<string> Candidates()
    {
        var scripts = RepositoryScan.Enumerate(Path.Combine(Root, "deploy", "scripts"), "*")
            .Where(path => path.EndsWith(".sh", StringComparison.Ordinal)
                || (path.EndsWith(".mjs", StringComparison.Ordinal) && !path.EndsWith(".test.mjs", StringComparison.Ordinal)));
        var pipelines = RepositoryScan.Enumerate(Path.Combine(Root, ".pipeline"), "*")
            .Where(path => path.EndsWith(".yaml", StringComparison.Ordinal)
                || path.EndsWith(".yml", StringComparison.Ordinal)
                || path.EndsWith(".conf", StringComparison.Ordinal));
        return scripts.Concat(pipelines).Order(StringComparer.Ordinal);
    }

}
