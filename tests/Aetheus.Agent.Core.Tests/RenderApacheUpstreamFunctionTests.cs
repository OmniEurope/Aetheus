// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>render-apache-upstream.sh</c>, which the deleted
/// <c>DeploymentSafetyAuditTests.BlueGreenApacheUpstream_IsRenderedFromVersionedTemplate</c> pinned by
/// reading its text. Here the same protections are exercised: only the versioned template is accepted,
/// the destination must equal the environment's <c>UPSTREAM_CONF</c> (a file in a sites-available
/// directory), the Define name is upper case, digits and underscores, ports are digits within range,
/// and a token left in the output is refused. The template used is the shape of
/// <c>.pipeline/configs/apache/aetheus-upstream.conf</c>.
/// </summary>
public sealed class RenderApacheUpstreamFunctionTests : ProductionFunctionTestBase
{
    private const string Template = """
        # Managed by Aetheus from .pipeline/configs/apache/aetheus-upstream.conf.
        # #{UPSTREAM_DEFINE}# names the environment.
        Define #{UPSTREAM_DEFINE}#_FRONT_PORT #{FRONT_PORT}#
        Define #{UPSTREAM_DEFINE}#_BACK_PORT #{BACK_PORT}#
        # Active colour: #{ACTIVE_COLOR}#

        """;

    private const string TemplateRelative = ".pipeline/configs/apache/aetheus-upstream.conf";

    private readonly string _upstreamConf;

    public RenderApacheUpstreamFunctionTests()
        : base("render-apache-upstream")
    {
        Harness.Write("ws/" + TemplateRelative, Template.ReplaceLineEndings("\n"));
        _upstreamConf = PathIn("apache/sites-available/aetheus.prod");
        Directory.CreateDirectory(Path.GetDirectoryName(_upstreamConf)!);
        Parameters["template"] = TemplateRelative;
        Parameters["output"] = _upstreamConf;
        Parameters["define"] = "AETHEUS";
        Parameters["upstream_conf"] = _upstreamConf;
        Parameters["front_port"] = "18080";
        Parameters["back_port"] = "18081";
        Parameters["active_color"] = "blue";
    }

    private static RenderApacheUpstreamFunction Function() => new();

    private string[] OutputDirectoryEntries() =>
        Directory.GetFileSystemEntries(Path.GetDirectoryName(_upstreamConf)!).Select(Path.GetFileName).Select(name => name!).ToArray();

    [Fact]
    public async Task TheVersionedTemplate_IsRenderedToTheEnvironmentsUpstreamFile()
    {
        await SucceedAsync(Function());

        Assert.Equal(
            "# Managed by Aetheus from .pipeline/configs/apache/aetheus-upstream.conf.\n"
            + "# AETHEUS names the environment.\n"
            + "Define AETHEUS_FRONT_PORT 18080\n"
            + "Define AETHEUS_BACK_PORT 18081\n"
            + "# Active colour: blue\n",
            Read(_upstreamConf));
        Assert.Equal(["aetheus.prod"], OutputDirectoryEntries());
    }

    [Fact]
    public async Task TheDefineNameAndTheUpstreamFile_DefaultToTheirVariables()
    {
        Parameters.Remove("define");
        Parameters.Remove("upstream_conf");
        Env["UPSTREAM_DEFINE"] = "AETHEUS_DEMO";
        Env["UPSTREAM_CONF"] = _upstreamConf;

        await SucceedAsync(Function());

        Assert.Contains("Define AETHEUS_DEMO_FRONT_PORT 18080", Read(_upstreamConf), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefineOnly_ResolvesTheDefineNameAndLeavesThePortsToTheSwitch()
    {
        var copy = PathIn("run-local.conf");
        Parameters["define_only"] = "true";
        Parameters["output"] = copy;
        Parameters.Remove("front_port");
        Parameters.Remove("back_port");
        Parameters.Remove("active_color");
        Parameters.Remove("upstream_conf");

        await SucceedAsync(Function());

        Assert.Equal(
            Template.Replace("#{UPSTREAM_DEFINE}#", "AETHEUS", StringComparison.Ordinal).ReplaceLineEndings("\n"),
            Read(copy));
        Assert.False(File.Exists(_upstreamConf));
    }

    [Fact]
    public async Task DefineOnly_WritesAWorkspaceRelativeOutputInTheWorkspace()
    {
        Parameters["define_only"] = "true";
        Parameters["output"] = ".bluegreen-upstream.conf";

        await SucceedAsync(Function());

        Assert.Contains("Define AETHEUS_FRONT_PORT #{FRONT_PORT}#", Read(Path.Combine(Workspace, ".bluegreen-upstream.conf")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FRONT_PORT", "The upstream template lost its front-port placeholder.")]
    [InlineData("BACK_PORT", "The upstream template lost its back-port placeholder.")]
    public async Task DefineOnly_RefusesACopyWithNothingLeftToSubstitute(string lost, string refusal)
    {
        Harness.Write("ws/" + TemplateRelative, Template.Replace("#{" + lost + "}#", "7", StringComparison.Ordinal).ReplaceLineEndings("\n"));
        var copy = PathIn("run-local.conf");
        Parameters["define_only"] = "true";
        Parameters["output"] = copy;

        Assert.Equal(refusal, await FailureAsync(Function()));

        Assert.False(File.Exists(copy));
    }

    [Fact]
    public async Task DefineOnly_RefusesADefineNameThatSurvivesItsOwnSubstitution()
    {
        // #{UPSTREAM_#{UPSTREAM_DEFINE}#}# becomes #{UPSTREAM_DEFINE}# once the inner token is replaced.
        Harness.Write("ws/" + TemplateRelative, "#{FRONT_PORT}# #{BACK_PORT}# #{UPSTREAM_#{UPSTREAM_DEFINE}#}#\n");
        Parameters["define_only"] = "true";
        Parameters["output"] = PathIn("run-local.conf");
        Parameters["define"] = "DEFINE";

        Assert.Equal("The Define name was not resolved.", await FailureAsync(Function()));
    }

    [Theory]
    [InlineData("/etc/apache2/other.conf")]
    [InlineData("other-upstream.conf")]
    [InlineData(".pipeline/configs/apache/aetheus-upstream.conf.bak")]
    public async Task ATemplateThatIsNotTheVersionedOne_IsRefused(string template)
    {
        Harness.Write("ws/" + template.TrimStart('/'), Template);
        Parameters["template"] = Path.IsPathRooted(template) ? Path.Combine(Workspace, template.TrimStart('/')) : template;

        Assert.Equal($"Unexpected Apache upstream template: {Parameters["template"]}", await FailureAsync(Function()));

        Assert.False(File.Exists(_upstreamConf));
    }

    [Fact]
    public async Task AMissingTemplate_IsRefused()
    {
        File.Delete(Path.Combine(Workspace, TemplateRelative));

        Assert.Equal($"Apache upstream template not found: {TemplateRelative}", await FailureAsync(Function()));
    }

    [Theory]
    [InlineData("aetheus", "UPSTREAM_DEFINE must start with an upper-case letter: 'aetheus'")]
    [InlineData("1AETHEUS", "UPSTREAM_DEFINE must start with an upper-case letter: '1AETHEUS'")]
    [InlineData("AETHEUS-PROD", "UPSTREAM_DEFINE must be upper case, digits and underscores: 'AETHEUS-PROD'")]
    [InlineData("AETHEUS$(id)", "UPSTREAM_DEFINE must be upper case, digits and underscores: 'AETHEUS$(id)'")]
    [InlineData("AETHEUS.PROD", "UPSTREAM_DEFINE must be upper case, digits and underscores: 'AETHEUS.PROD'")]
    public async Task ADefineNameThatIsNotUpperCaseDigitsAndUnderscores_IsRefused(string define, string refusal)
    {
        Parameters["define"] = define;

        Assert.Equal(refusal, await FailureAsync(Function()));

        Assert.False(File.Exists(_upstreamConf));
    }

    [Fact]
    public async Task NoDefineName_IsRefused()
    {
        Parameters.Remove("define");

        Assert.Equal("UPSTREAM_DEFINE must start with an upper-case letter: ''", await FailureAsync(Function()));
    }

    [Theory]
    [InlineData("/etc/apache2/aetheus.prod")]
    [InlineData("/etc/apache2/sites-available")]
    [InlineData("")]
    public async Task AnUpstreamFileOutsideASitesAvailableDirectory_IsRefused(string conf)
    {
        Parameters["upstream_conf"] = conf;
        Parameters["output"] = conf.Length == 0 ? _upstreamConf : conf;
        if (conf.Length == 0)
            Parameters.Remove("upstream_conf");

        Assert.Equal($"UPSTREAM_CONF must name a file in a sites-available directory: '{conf}'", await FailureAsync(Function()));
    }

    [Fact]
    public async Task AnUpstreamFileWithATraversal_IsRefused()
    {
        var conf = "/srv/sites-available/../../etc/aetheus.prod";
        Parameters["upstream_conf"] = conf;
        Parameters["output"] = conf;

        Assert.Equal($"UPSTREAM_CONF must not contain a traversal: {conf}", await FailureAsync(Function()));
    }

    [Fact]
    public async Task ADestinationThatIsNotTheEnvironmentsUpstreamFile_IsRefused()
    {
        var other = PathIn("apache/sites-available/other.conf");
        Parameters["output"] = other;

        Assert.Equal($"Unexpected Apache upstream destination: {other} (expected {_upstreamConf})", await FailureAsync(Function()));

        Assert.False(File.Exists(other));
        Assert.False(File.Exists(_upstreamConf));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("Blue")]
    [InlineData("")]
    public async Task AColourOtherThanBlueOrGreen_IsRefused(string color)
    {
        Parameters["active_color"] = color;

        Assert.Equal($"Invalid active colour: {color}", await FailureAsync(Function()));
    }

    [Theory]
    [InlineData("front_port", "", "Invalid front port: ")]
    [InlineData("front_port", "80a", "Invalid front port: 80a")]
    [InlineData("front_port", "-1", "Invalid front port: -1")]
    [InlineData("front_port", "8 0", "Invalid front port: 8 0")]
    [InlineData("back_port", "", "Invalid back port: ")]
    [InlineData("back_port", "9x", "Invalid back port: 9x")]
    [InlineData("front_port", "0", "Front port is out of range.")]
    [InlineData("front_port", "65536", "Front port is out of range.")]
    [InlineData("front_port", "99999999999999999999999", "Front port is out of range.")]
    [InlineData("back_port", "0", "Back port is out of range.")]
    [InlineData("back_port", "70000", "Back port is out of range.")]
    public async Task APortThatIsNotDigitsWithinRange_IsRefused(string parameter, string port, string refusal)
    {
        Parameters[parameter] = port;

        Assert.Equal(refusal, await FailureAsync(Function()));

        Assert.False(File.Exists(_upstreamConf));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    [InlineData("0080")]
    public async Task APortWithinRange_IsAccepted(string port)
    {
        Parameters["front_port"] = port;

        await SucceedAsync(Function());

        Assert.Contains($"_FRONT_PORT {port}\n", Read(_upstreamConf), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenLeftInTheOutput_IsRefused_AndTheDestinationKeepsWhatItHeld()
    {
        Harness.Write("ws/" + TemplateRelative, Template.Replace("# Active colour", "# #{SOMETHING_ELSE}# colour", StringComparison.Ordinal).ReplaceLineEndings("\n"));
        File.WriteAllText(_upstreamConf, "Define AETHEUS_FRONT_PORT 1\n");

        Assert.Equal("Apache upstream template contains an unresolved token.", await FailureAsync(Function()));

        Assert.Equal("Define AETHEUS_FRONT_PORT 1\n", Read(_upstreamConf));
        Assert.Equal(["aetheus.prod"], OutputDirectoryEntries());
    }

    [Fact]
    public async Task AnExistingUpstreamFile_IsReplacedWhole_NoTemporaryCopyLeftBehind()
    {
        File.WriteAllText(_upstreamConf, "stale\n");

        await SucceedAsync(Function());

        Assert.DoesNotContain("stale", Read(_upstreamConf), StringComparison.Ordinal);
        Assert.Equal(["aetheus.prod"], OutputDirectoryEntries());
    }
}
