// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Verifies UTC installation-marker parsing, offset conversion and the non-throwing
/// assembly-time fallback.</summary>
public class InstallInfoReaderTests : IDisposable
{
    private readonly string _workDir;

    public InstallInfoReaderTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "prom-install-info-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private void WriteMarker(string content)
        => File.WriteAllText(Path.Combine(_workDir, InstallInfoReader.MarkerFileName), content);

    private static DateTime AssemblyMtimeFallback()
        => File.GetLastWriteTimeUtc(typeof(InstallInfoReader).Assembly.Location);

    [Fact]
    public void Read_UtcMarker_ReturnsParsedUtcValue()
    {
        WriteMarker("2026-07-06T10:30:00Z");

        var result = InstallInfoReader.Read(_workDir);

        Assert.Equal(new DateTime(2026, 7, 6, 10, 30, 0, DateTimeKind.Utc), result);
        Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
    }

    [Fact]
    public void Read_MarkerWithLocalOffset_AdjustsToUtc()
    {
        // Hand-edited local-tz timestamp: +02:00 must land on 08:15 UTC.
        WriteMarker("2026-07-06T10:15:00+02:00");

        var result = InstallInfoReader.Read(_workDir);

        Assert.Equal(new DateTime(2026, 7, 6, 8, 15, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Read_MarkerWithSurroundingWhitespace_StillParses()
    {
        WriteMarker("  2026-07-06T10:30:00Z\n");

        var result = InstallInfoReader.Read(_workDir);

        Assert.Equal(new DateTime(2026, 7, 6, 10, 30, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Read_CorruptMarker_FallsBackToAssemblyMtime()
    {
        WriteMarker("not-a-timestamp");

        var result = InstallInfoReader.Read(_workDir);

        Assert.Equal(AssemblyMtimeFallback(), result);
    }

    [Fact]
    public void Read_EmptyMarker_FallsBackToAssemblyMtime()
    {
        WriteMarker("   ");

        var result = InstallInfoReader.Read(_workDir);

        Assert.Equal(AssemblyMtimeFallback(), result);
    }

    [Fact]
    public void Read_MissingMarkerFile_FallsBackToAssemblyMtime()
    {
        var result = InstallInfoReader.Read(_workDir); // dir exists, marker doesn't

        Assert.Equal(AssemblyMtimeFallback(), result);
    }

    [Fact]
    public void Read_MarkerReplacedByDirectory_FallsBackToAssemblyMtime()
    {
        Directory.CreateDirectory(Path.Combine(_workDir, InstallInfoReader.MarkerFileName));

        var result = InstallInfoReader.Read(_workDir);

        Assert.Equal(AssemblyMtimeFallback(), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_MissingWorkDirectory_FallsBackToAssemblyMtime(string? workDir)
    {
        var result = InstallInfoReader.Read(workDir);

        Assert.Equal(AssemblyMtimeFallback(), result);
    }
}
