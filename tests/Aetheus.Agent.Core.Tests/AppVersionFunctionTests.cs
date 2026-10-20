// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/app-version.sh</c>, run through the real executor on a temp
/// workspace unique to each test: the composed version, the parameter and variable inputs, and the
/// script's refusals with its messages.
/// </summary>
public sealed class AppVersionFunctionTests : IDisposable
{
    private const string Props = "<Project>\n  <PropertyGroup>\n    <VersionPrefix>1.3.0</VersionPrefix>\n  </PropertyGroup>\n</Project>\n";

    private readonly FunctionTestHarness _harness = new("aetheus-app-version");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal) { ["BUILD_RELEASENUMBER"] = "42" };
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public void Dispose() => _harness.Dispose();

    private void WriteProps(string content) => _harness.Write("ws/Directory.Build.props", content);

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new AppVersionFunction(), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, variables) = await RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Empty(variables);
        return result.FailureReason!;
    }

    [Theory]
    [InlineData("<VersionPrefix>1.3.0</VersionPrefix>", "", "1.3.42")]
    [InlineData("<VersionPrefix>2.10</VersionPrefix>", "-nightly.1", "2.10.42-nightly.1")]
    [InlineData("<VersionPrefix>1.2</VersionPrefix><VersionPrefix>3.4</VersionPrefix>", "_rc", "3.4.42_rc")]
    public async Task Version_IsMajorMinorReleaseAndSuffix(string prefixLine, string suffix, string expected)
    {
        WriteProps("<Project>\r\n" + prefixLine + "\r\n</Project>");
        _env["VERSION_SUFFIX"] = suffix;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, variables["APP_VERSION"]);
    }

    [Fact]
    public async Task Parameters_WinOverTheScriptVariables()
    {
        WriteProps(Props);
        _env["VERSION_SUFFIX"] = "-ignored";
        _parameters["run_number"] = "7";
        _parameters["suffix"] = "-qa";

        var (_, variables) = await RunAsync();

        Assert.Equal("1.3.7-qa", variables["APP_VERSION"]);
    }

    [Fact]
    public async Task ReleaseNumberAllocatedInTheSeriesOfTheProps_Composes()
    {
        WriteProps("<VersionPrefix>1.4.0</VersionPrefix>");
        _env["BUILD_RELEASENUMBER"] = "0";
        _env["BUILD_RELEASESERIES"] = "1.4";

        var (_, variables) = await RunAsync();

        Assert.Equal("1.4.0", variables["APP_VERSION"]);
    }

    [Fact]
    public async Task ReleaseNumberAllocatedInAnotherSeriesThanTheProps_IsRefused()
    {
        WriteProps("<VersionPrefix>1.4.0</VersionPrefix>");
        _env["BUILD_RELEASESERIES"] = "1.3";

        var reason = await FailureAsync();

        Assert.Contains("series 1.3", reason, StringComparison.Ordinal);
        Assert.Contains("series 1.4", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitRunNumber_IsNotCheckedAgainstTheAllocatedSeries()
    {
        WriteProps("<VersionPrefix>1.4.0</VersionPrefix>");
        _env["BUILD_RELEASESERIES"] = "1.3";
        _parameters["run_number"] = "7";

        var (_, variables) = await RunAsync();

        Assert.Equal("1.4.7", variables["APP_VERSION"]);
    }
    [Fact]
    public async Task MissingProps_FailsWithThePathTheScriptPrinted()
    {
        Assert.EndsWith(
            $"Directory.Build.props not found at {_harness.Workspace}/Directory.Build.props.",
            await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4a")]
    [InlineData("4\n2")]
    public async Task RunNumberOtherThanDigits_IsRefused(string runNumber)
    {
        WriteProps(Props);
        _env["BUILD_RELEASENUMBER"] = runNumber;

        Assert.EndsWith($"The run number must be digits only, got '{runNumber}'.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("+x")]
    [InlineData("a b")]
    [InlineData("-x\n")]
    public async Task SuffixWithUnexpectedCharacters_IsRefused(string suffix)
    {
        WriteProps(Props);
        _env["VERSION_SUFFIX"] = suffix;

        Assert.EndsWith($"VERSION_SUFFIX contains unexpected characters: '{suffix}'.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<Project/>\n", 0)]
    [InlineData("<VersionPrefix>1.2</VersionPrefix>\n<VersionPrefix>1.3</VersionPrefix>\n", 2)]
    public async Task NotExactlyOnePrefixLine_IsRefused(string props, int found)
    {
        WriteProps(props);

        Assert.EndsWith(
            $"Directory.Build.props must declare exactly one <VersionPrefix>, found {found}.",
            await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConditionalPrefixWithAttributes_IsNotCountedLikeGrep()
    {
        WriteProps("<VersionPrefix>1.2</VersionPrefix>\n<VersionPrefix Condition=\"x\">1.3</VersionPrefix>\n");

        var (_, variables) = await RunAsync();

        Assert.Equal("1.2.42", variables["APP_VERSION"]);
    }

    [Theory]
    [InlineData("<VersionPrefix>1.2.3.4</VersionPrefix>")]
    [InlineData("<VersionPrefix>one.two</VersionPrefix>")]
    [InlineData("<VersionPrefix>1</VersionPrefix>")]
    public async Task PrefixNotMajorMinorPatch_IsRefused(string line)
    {
        WriteProps(line + "\n");

        Assert.EndsWith(
            "The <VersionPrefix> of Directory.Build.props is not <major>.<minor>[.<patch>].",
            await FailureAsync(), StringComparison.Ordinal);
    }
}
