// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R-36: levels given to the lines tools really write on standard error.</summary>
public class StderrLineClassifierTests
{
    // The lines of the recette row: nothing in them is an error.
    [Theory]
    [InlineData("Volume aetheus-qa-rollback-2848-7c1e Creating")]
    [InlineData("Network aetheus-qa-rollback-2848-7c1e_default Creating")]
    [InlineData("Container aetheus-qa-rollback-2848-7c1e-database-1 Started")]
    [InlineData("Using config from .jscpd.json")]
    [InlineData("Found 0 clones, 0 duplicated lines in 214 files (0%)")]
    [InlineData("Errors: 0, warnings: 0")]
    [InlineData("Info about the scan follows")]
    public void ALineWithoutAStatedSeverity_IsAWarning(string line) =>
        Assert.Equal(TaskLogLevel.Warning, ExecutorHelper.ClassifyLine(line, TaskLogLevel.Error));

    [Theory]
    [InlineData("INFO Detected OS family=\"ubuntu\"")]
    [InlineData("2026-10-22T10:45:12Z\tINFO\tDetected OS family=\"ubuntu\" version=\"22.04\"")]
    [InlineData("\u001b[34mINFO\u001b[0m Number of language-specific files num=3")]
    [InlineData("time=\"2026-10-22T10:45:12Z\" level=info msg=\"pulling\"")]
    [InlineData("[info] checking out")]
    public void ALineThatStatesInfo_IsInfo(string line) =>
        Assert.Equal(TaskLogLevel.Info, ExecutorHelper.ClassifyLine(line, TaskLogLevel.Error));

    [Theory]
    [InlineData("WARN Using severities from other vendors")]
    [InlineData("2026-10-22T10:45:12Z\tWARN\tThe vulnerability database is old")]
    [InlineData("warning: LF will be replaced by CRLF")]
    [InlineData("npm warn deprecated left-pad@1.0.0")]
    [InlineData("Program.cs(3,5): warning CS8618: Non-nullable property")]
    [InlineData("W: Failed to fetch http://archive.example/dists/InRelease")]
    public void ALineThatStatesAWarning_IsAWarning(string line) =>
        Assert.Equal(TaskLogLevel.Warning, ExecutorHelper.ClassifyLine(line, TaskLogLevel.Error));

    [Theory]
    [InlineData("ERROR the scan failed")]
    [InlineData("FATAL\tscan error: no such image")]
    [InlineData("2026-10-22T10:45:12Z\tFATAL\tFailed to initialize a scanner")]
    [InlineData("\u001b[31mFATAL\u001b[0m Run error")]
    [InlineData("error: Connection refused while contacting the database")]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git")]
    [InlineData("Error response from daemon: pull access denied for aetheus/missing")]
    [InlineData("npm ERR! code E404")]
    [InlineData("npm error code E404")]
    [InlineData("E: Unable to locate package nginx-extra")]
    [InlineData("time=\"2026-10-22T10:45:12Z\" level=error msg=\"failed\"")]
    [InlineData("{\"level\":\"error\",\"msg\":\"boom\"}")]
    [InlineData("Program.cs(3,5): error CS1002: ; expected")]
    [InlineData("panic: runtime error: index out of range")]
    public void ALineThatStatesAnError_IsAnError(string line) =>
        Assert.Equal(TaskLogLevel.Error, ExecutorHelper.ClassifyLine(line, TaskLogLevel.Error));

    [Theory]
    [InlineData("Hint: You are currently not seeing messages from other users and the system.")]
    [InlineData("-- No entries --")]
    public void TheJournalctlNotices_StayInfo(string line) =>
        Assert.Equal(TaskLogLevel.Info, ExecutorHelper.ClassifyLine(line, TaskLogLevel.Error));

    // Only the standard-error channel is reconsidered.
    [Theory]
    [InlineData(TaskLogLevel.Info)]
    [InlineData(TaskLogLevel.Warning)]
    [InlineData(TaskLogLevel.Debug)]
    public void ALineNotComingFromStandardError_IsNeverReconsidered(TaskLogLevel level) =>
        Assert.Equal(level, ExecutorHelper.ClassifyLine("ERROR looks like an error but is not on stderr", level));
}
