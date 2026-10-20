// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Every value below reaches either an argv the agent runs or a path it writes to, and all of them
/// come from pipeline YAML a project author edits. So the point of these tests is not that valid
/// input is accepted, it is that input naming somewhere outside the workspace is refused rather
/// than normalised into something that looks fine.
/// </summary>
public sealed class PipelineDotnetTestRequestTests
{
    private static Dictionary<string, string> Valid() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["WORKSPACE"] = Path.Combine(Path.GetTempPath(), "ws"),
        [PipelineDotnetTestVariables.ResultsDirectory] = "coverage/backend",
        [PipelineDotnetTestVariables.StatusVariable] = "BACKEND_STATUS"
    };

    [Fact]
    public void AWellFormedStepIsAccepted()
    {
        Assert.True(PipelineDotnetTestRequest.TryCreate(
            "tests/Aetheus.Back.Tests", Valid(), out var request, out var rejection));

        Assert.Null(rejection);
        Assert.Equal("tests/Aetheus.Back.Tests", request!.Project);
        Assert.Equal("BACKEND_STATUS", request.StatusVariable);
        Assert.False(request.CollectCoverage);
        Assert.Equal("Release", request.Configuration);
        Assert.Equal("results.trx", request.TrxName);
    }

    [Fact]
    public void TheTrxPathIsResolvedUnderTheResultsDirectory()
    {
        var vars = Valid();
        vars[PipelineDotnetTestVariables.TrxName] = "backend.trx";

        Assert.True(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out var request, out _));

        Assert.Equal(
            Path.Combine(vars["WORKSPACE"], "coverage/backend", "backend.trx"), request!.TrxPath);
    }

    [Theory]
    [InlineData("../../../etc")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows")]
    [InlineData("tests/../../escape")]
    public void AProjectPathLeavingTheWorkspaceIsRefused(string project)
    {
        Assert.False(PipelineDotnetTestRequest.TryCreate(project, Valid(), out _, out var rejection));
        Assert.Contains("stay in the workspace", rejection!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/var/tmp")]
    public void AResultsDirectoryLeavingTheWorkspaceIsRefused(string results)
    {
        var vars = Valid();
        vars[PipelineDotnetTestVariables.ResultsDirectory] = results;

        Assert.False(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out _, out var rejection));
        Assert.Contains("inside the workspace", rejection!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lower_case")]
    [InlineData("HAS SPACE")]
    [InlineData("HAS-DASH")]
    [InlineData("1LEADING_DIGIT")]
    [InlineData("")]
    public void AStatusVariableThatIsNotAnIdentifierIsRefused(string name)
    {
        var vars = Valid();
        vars[PipelineDotnetTestVariables.StatusVariable] = name;

        Assert.False(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out _, out var rejection));
        Assert.Contains("upper-case", rejection!, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrxNameCarryingAPathIsRefused()
    {
        var vars = Valid();
        vars[PipelineDotnetTestVariables.TrxName] = "../escape.trx";

        Assert.False(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out _, out var rejection));
        Assert.Contains("plain file name", rejection!, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunSettingsPathLeavingTheWorkspaceIsRefused()
    {
        var vars = Valid();
        vars[PipelineDotnetTestVariables.RunSettings] = "../../evil.runsettings";

        Assert.False(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out _, out var rejection));
        Assert.Contains("inside the workspace", rejection!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingWorkspaceIsRefused()
    {
        var vars = Valid();
        vars.Remove("WORKSPACE");

        Assert.False(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out _, out var rejection));
        Assert.Contains("WORKSPACE", rejection!, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageIsOnlyCollectedWhenExplicitlyRequested()
    {
        var vars = Valid();
        vars[PipelineDotnetTestVariables.CollectCoverage] = "TRUE";

        Assert.True(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out var request, out _));
        Assert.True(request!.CollectCoverage);
    }

    // --- Toolchain: pinned SDK, tool directories, progress interval ---

    private static string Absolute(params string[] parts) => Path.Combine([Path.GetTempPath(), .. parts]);

    [Fact]
    public void WithoutToolchainSettingsTheHostDotnetRunsWithTheDefaultProgressInterval()
    {
        Assert.True(PipelineDotnetTestRequest.TryCreate("tests/X", Valid(), out var request, out _));

        Assert.Equal("dotnet", request!.DotnetPath);
        Assert.Empty(request.ToolPaths);
        Assert.Equal(TimeSpan.FromSeconds(30), request.ProgressInterval);
    }

    [Fact]
    public void APinnedSdkToolDirectoriesAndAnIntervalAreRead()
    {
        var vars = Valid();
        var dotnet = Absolute("sdk", "dotnet");
        vars[PipelineDotnetTestVariables.DotnetPath] = dotnet;
        vars[PipelineDotnetTestVariables.ToolPaths] = Absolute("node", "bin") + "\n" + Absolute("tools");
        vars[PipelineDotnetTestVariables.ProgressIntervalSeconds] = "45";

        Assert.True(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out var request, out var rejection), rejection);

        Assert.Equal(dotnet, request!.DotnetPath);
        Assert.Equal([Absolute("node", "bin"), Absolute("tools")], request.ToolPaths);
        Assert.Equal(TimeSpan.FromSeconds(45), request.ProgressInterval);
    }

    [Theory]
    [InlineData("$(DOTNET)")]
    [InlineData("sdk/dotnet")]
    [InlineData("pwsh")]
    public void ADotnetThatIsNeitherTheHostOneNorAnAbsolutePathIsRefused(string dotnet)
    {
        // An unresolved $(DOTNET) means the SDK output was never published: falling back to the host
        // SDK would hide that, so the step refuses it.
        var vars = Valid();
        vars[PipelineDotnetTestVariables.DotnetPath] = dotnet;

        Assert.False(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out _, out var rejection));
        Assert.Contains(PipelineDotnetTestVariables.DotnetPath, rejection!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$(NODE_BIN_DIR)")]
    [InlineData("relative/bin")]
    [InlineData("")]
    public void AToolDirectoryThatIsNotAbsoluteIsRefused(string entry)
    {
        var vars = Valid();
        vars[PipelineDotnetTestVariables.ToolPaths] = Absolute("node") + "\n" + entry;

        Assert.False(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out _, out var rejection));
        Assert.Contains(PipelineDotnetTestVariables.ToolPaths, rejection!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("ten")]
    [InlineData("1.5")]
    public void AProgressIntervalThatIsNotAPositiveIntegerIsRefused(string interval)
    {
        var vars = Valid();
        vars[PipelineDotnetTestVariables.ProgressIntervalSeconds] = interval;

        Assert.False(PipelineDotnetTestRequest.TryCreate("tests/X", vars, out _, out var rejection));
        Assert.Contains(PipelineDotnetTestVariables.ProgressIntervalSeconds, rejection!, StringComparison.Ordinal);
    }
}
