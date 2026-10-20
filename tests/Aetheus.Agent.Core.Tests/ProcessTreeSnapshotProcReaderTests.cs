// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The Linux process snapshot reads each process's parent from its <c>stat</c> file under
/// <c>/proc</c>. The reader takes the directory, so the parsing (command names holding spaces and
/// parentheses included) and the tolerance to vanished or unreadable processes are asserted on a temporary
/// directory on any platform.</summary>
public sealed class ProcessTreeSnapshotProcReaderTests : IDisposable
{
    private readonly DirectoryInfo _proc = Directory.CreateTempSubdirectory("aetheus-proc-");

    public void Dispose() => _proc.Delete(recursive: true);

    private void Process(string name, string? stat)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_proc.FullName, name)).FullName;
        if (stat is not null)
            File.WriteAllText(Path.Combine(directory, "stat"), stat);
    }

    [Fact]
    public void EachNumericDirectory_IsMappedToTheParentFoundAfterItsCommandName()
    {
        Process("100", "100 (bash) S 1 100 100 0 -1 4194560");
        Process("200", "200 (dotnet) S 100 200 100 0 -1 4194560");

        var parents = ProcessTreeSnapshot.ReadParentsFromProc(_proc.FullName);

        Assert.Equal(new Dictionary<int, int> { [100] = 1, [200] = 100 }, parents);
    }

    [Fact]
    public void ACommandNameWithSpacesAndParentheses_DoesNotShiftTheFields()
    {
        Process("300", "300 (my (odd) cmd) R 42 300 300 0 -1 0");

        var parents = ProcessTreeSnapshot.ReadParentsFromProc(_proc.FullName);

        Assert.Equal(42, Assert.Single(parents).Value);
    }

    [Fact]
    public void DirectoriesThatAreNotProcesses_AreIgnored()
    {
        Process("self", "1 (init) S 0 1 1 0 -1 0");
        Process("sys", null);
        Process("400", "400 (sh) S 1 400 400 0 -1 0");

        var parents = ProcessTreeSnapshot.ReadParentsFromProc(_proc.FullName);

        Assert.Equal(400, Assert.Single(parents).Key);
    }

    [Theory]
    [InlineData("garbage without a parenthesis")]
    [InlineData("500 (sh)")]
    [InlineData("500 (sh) S")]
    [InlineData("500 (sh) S notanumber 500")]
    public void AMalformedStatFile_IsSkipped(string stat)
    {
        Process("500", stat);
        Process("600", "600 (sh) S 1 600 600 0 -1 0");

        var parents = ProcessTreeSnapshot.ReadParentsFromProc(_proc.FullName);

        Assert.Equal(600, Assert.Single(parents).Key);
    }

    [Fact]
    public void AProcessThatVanishedBeforeItsStatWasRead_IsSkipped()
    {
        Process("700", stat: null);
        Process("800", "800 (sh) S 1 800 800 0 -1 0");

        var parents = ProcessTreeSnapshot.ReadParentsFromProc(_proc.FullName);

        Assert.Equal(800, Assert.Single(parents).Key);
    }

    [Fact]
    public void AMissingProcDirectory_GivesAnEmptySnapshot()
    {
        var parents = ProcessTreeSnapshot.ReadParentsFromProc(Path.Combine(_proc.FullName, "absent"));

        Assert.Empty(parents);
    }
}
