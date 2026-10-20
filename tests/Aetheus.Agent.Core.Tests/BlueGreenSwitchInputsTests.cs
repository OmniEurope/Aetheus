// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The inputs a blue-green switch reads from its step, and the refusal naming the first
/// missing one, on a real template file.</summary>
public sealed class BlueGreenSwitchInputsTests : IDisposable
{
    private readonly string _template = Path.Combine(Path.GetTempPath(), $"bg-template-{Guid.NewGuid():N}.conf");

    public BlueGreenSwitchInputsTests() => File.WriteAllText(_template, "serve #{COLOR}#\n");

    public void Dispose() => File.Delete(_template);

    private Dictionary<string, string> Complete() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["AETHEUS_BG_REVISION"] = " c-new ",
        ["AETHEUS_BG_RELOAD_HELPER"] = "/usr/local/lib/aetheus/aetheus-apache-reload",
        ["AETHEUS_BG_UPSTREAM_TEMPLATE"] = _template,
        ["AETHEUS_BG_UPSTREAM_CONF"] = "/etc/apache2/aetheus-upstream.conf"
    };

    [Fact]
    public void CompleteInputs_AreReadTrimmed()
    {
        var inputs = BlueGreenSwitchInputs.Read(Complete(), out var refusal);

        Assert.Equal(string.Empty, refusal);
        Assert.Equal(
            new BlueGreenSwitchInputs("c-new", "/usr/local/lib/aetheus/aetheus-apache-reload", _template, "/etc/apache2/aetheus-upstream.conf"),
            inputs);
    }

    [Theory]
    [InlineData("AETHEUS_BG_REVISION", "AETHEUS_BG_REVISION is required so the journal records what was deployed.")]
    [InlineData("AETHEUS_BG_RELOAD_HELPER", "AETHEUS_BG_RELOAD_HELPER is required to apply the switch.")]
    [InlineData("AETHEUS_BG_UPSTREAM_TEMPLATE", "AETHEUS_BG_UPSTREAM_TEMPLATE and AETHEUS_BG_UPSTREAM_CONF are required: without them the switch would reload the live configuration unchanged.")]
    [InlineData("AETHEUS_BG_UPSTREAM_CONF", "AETHEUS_BG_UPSTREAM_TEMPLATE and AETHEUS_BG_UPSTREAM_CONF are required: without them the switch would reload the live configuration unchanged.")]
    public void AMissingInput_IsNamed(string missing, string expected)
    {
        var env = Complete();
        env[missing] = "  ";

        Assert.Null(BlueGreenSwitchInputs.Read(env, out var refusal));
        Assert.Equal(expected, refusal);
    }

    [Fact]
    public void AMissingTemplateFile_IsNamed()
    {
        var env = Complete();
        var absent = Path.Combine(Path.GetTempPath(), $"bg-absent-{Guid.NewGuid():N}.conf");
        env["AETHEUS_BG_UPSTREAM_TEMPLATE"] = absent;

        Assert.Null(BlueGreenSwitchInputs.Read(env, out var refusal));
        Assert.Equal($"Upstream template is missing: {absent}", refusal);
    }

    [Fact]
    public void TheRevisionIsCheckedFirst()
    {
        Assert.Null(BlueGreenSwitchInputs.Read(new Dictionary<string, string>(), out var refusal));
        Assert.Equal("AETHEUS_BG_REVISION is required so the journal records what was deployed.", refusal);
    }
}
