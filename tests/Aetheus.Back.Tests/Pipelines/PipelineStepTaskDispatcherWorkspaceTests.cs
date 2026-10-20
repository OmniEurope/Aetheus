// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// A matrix leg can land on a Windows runner while the stage's WORKSPACE was resolved for Linux.
/// Dispatching that leg with a POSIX path makes the agent fail at the first command, so the dispatcher
/// rewrites the path - but only when it actually has to, because rewriting a container or an
/// already-Windows workspace would corrupt a correct value.
///
/// These exercise the real method on real inputs and assert the resulting path, not that it was called.
/// </summary>
public class PipelineStepTaskDispatcherWorkspaceTests
{
    private const string PosixWorkspace = "/home/aetheus/work/42";

    private static Server WindowsServer() => new()
    {
        Id = 7,
        Name = "win-runner",
        OsType = OsType.Windows,
        OsDescription = "Windows Server 2022"
    };

    private static Server LinuxServer() => new()
    {
        Id = 8,
        Name = "linux-runner",
        OsType = OsType.Linux,
        OsDescription = "Ubuntu 24.04"
    };

    [Fact]
    public void WindowsLeg_WithPosixWorkspace_IsRewrittenToAWindowsPath()
    {
        var vars = new Dictionary<string, string> { ["WORKSPACE"] = PosixWorkspace };

        PipelineStepTaskDispatcher.NormalizeLegWorkspace(42, stageIsContainer: false, WindowsServer(), vars);

        Assert.NotEqual(PosixWorkspace, vars["WORKSPACE"]);
        Assert.Contains(":\\", vars["WORKSPACE"], StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerStage_KeepsItsWorkspace_EvenOnAWindowsHost()
    {
        // Container legs execute inside a Linux image; the host's OS is irrelevant and the mount point
        // must survive untouched.
        var vars = new Dictionary<string, string> { ["WORKSPACE"] = PosixWorkspace };

        PipelineStepTaskDispatcher.NormalizeLegWorkspace(42, stageIsContainer: true, WindowsServer(), vars);

        Assert.Equal(PosixWorkspace, vars["WORKSPACE"]);
    }

    [Fact]
    public void LinuxLeg_KeepsItsPosixWorkspace()
    {
        var vars = new Dictionary<string, string> { ["WORKSPACE"] = PosixWorkspace };

        PipelineStepTaskDispatcher.NormalizeLegWorkspace(42, stageIsContainer: false, LinuxServer(), vars);

        Assert.Equal(PosixWorkspace, vars["WORKSPACE"]);
    }

    [Fact]
    public void WindowsLeg_WithAnAlreadyWindowsWorkspace_IsLeftAlone()
    {
        const string windowsWorkspace = @"C:\aetheus\work\42";
        var vars = new Dictionary<string, string> { ["WORKSPACE"] = windowsWorkspace };

        PipelineStepTaskDispatcher.NormalizeLegWorkspace(42, stageIsContainer: false, WindowsServer(), vars);

        Assert.Equal(windowsWorkspace, vars["WORKSPACE"]);
    }

    [Fact]
    public void LegWithoutAWorkspace_GainsNone()
    {
        var vars = new Dictionary<string, string>();

        PipelineStepTaskDispatcher.NormalizeLegWorkspace(42, stageIsContainer: false, WindowsServer(), vars);

        Assert.False(vars.ContainsKey("WORKSPACE"));
    }

    [Fact]
    public void BuildIdVariable_WinsOverTheRunId_SoTheLegMatchesTheAgentsDirectory()
    {
        // The agent lays its workspace out by build id, so when the run carries one the rewritten path
        // must use it and not the run id - otherwise the leg points at a directory the agent never made.
        var vars = new Dictionary<string, string>
        {
            ["WORKSPACE"] = PosixWorkspace,
            ["BUILD_BUILDID"] = "9001"
        };

        PipelineStepTaskDispatcher.NormalizeLegWorkspace(42, stageIsContainer: false, WindowsServer(), vars);

        Assert.Equal(PipelineCommandBuilder.GetDefaultWorkspace(9001, isWindows: true), vars["WORKSPACE"]);
        Assert.NotEqual(PipelineCommandBuilder.GetDefaultWorkspace(42, isWindows: true), vars["WORKSPACE"]);
    }

    [Fact]
    public void UnparsableBuildId_FallsBackToTheRunId_RatherThanThrowing()
    {
        var vars = new Dictionary<string, string>
        {
            ["WORKSPACE"] = PosixWorkspace,
            ["BUILD_BUILDID"] = "not-a-number"
        };

        PipelineStepTaskDispatcher.NormalizeLegWorkspace(42, stageIsContainer: false, WindowsServer(), vars);

        Assert.Equal(PipelineCommandBuilder.GetDefaultWorkspace(42, isWindows: true), vars["WORKSPACE"]);
    }

    /// <summary>Missing or invalid BUILD_BUILDID values fall back to the run identifier so work
    /// uses the correct workspace.</summary>
    [Fact]
    public void AbsentBuildId_FallsBackToTheRunId_NeverToRunZero()
    {
        var vars = new Dictionary<string, string> { ["WORKSPACE"] = PosixWorkspace };

        PipelineStepTaskDispatcher.NormalizeLegWorkspace(42, stageIsContainer: false, WindowsServer(), vars);

        Assert.Equal(PipelineCommandBuilder.GetDefaultWorkspace(42, isWindows: true), vars["WORKSPACE"]);
        Assert.NotEqual(PipelineCommandBuilder.GetDefaultWorkspace(0, isWindows: true), vars["WORKSPACE"]);
    }
}
