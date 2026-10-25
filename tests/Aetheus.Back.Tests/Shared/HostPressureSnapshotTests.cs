// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.Tests;

/// <summary>
/// R-6: the host pressure (Linux PSI) a slow-request line carries is parsed from /proc/pressure files,
/// and an absent or unreadable source reads as unknown instead of throwing.
/// </summary>
public sealed class HostPressureSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aetheus-psi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private void Write(string name, string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, name), content);
    }

    [Fact]
    public void ACpuFile_GivesTheSomeAvg10_AndNoFullLine()
    {
        var pressure = HostPressureSnapshot.Parse("some avg10=12.50 avg60=3.00 avg300=1.00 total=123456\n");

        Assert.Equal(12.5, pressure.Some);
        Assert.Null(pressure.Full);
    }

    [Fact]
    public void AnIoFile_GivesBothTheSomeAndTheFullAvg10()
    {
        var pressure = HostPressureSnapshot.Parse(
            "some avg10=45.67 avg60=20.00 avg300=5.00 total=999\nfull avg10=30.10 avg60=12.00 avg300=2.00 total=500\n");

        Assert.Equal(45.67, pressure.Some);
        Assert.Equal(30.10, pressure.Full);
    }

    [Fact]
    public void WindowsLineEndings_AreAccepted()
    {
        var pressure = HostPressureSnapshot.Parse("some avg10=1.00 avg60=0 avg300=0 total=1\r\nfull avg10=0.25 avg60=0 avg300=0 total=1\r\n");

        Assert.Equal(1.0, pressure.Some);
        Assert.Equal(0.25, pressure.Full);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("not pressure at all")]
    [InlineData("some avg60=1.0 total=5")]
    [InlineData("some avg10=abc avg60=1.0 total=5")]
    [InlineData("some avg10=NaN avg60=1.0 total=5")]
    [InlineData("some\nfull")]
    public void GarbageOrIncompleteContent_IsUnknown_AndNeverThrows(string? content)
    {
        Assert.Equal(ResourcePressure.Unavailable, HostPressureSnapshot.Parse(content));
    }

    [Fact]
    public void AMalformedFullLine_DoesNotSpoilTheValidSomeLine()
    {
        var pressure = HostPressureSnapshot.Parse("some avg10=7.5 avg60=0 total=1\nfull avg10=oops total=1");

        Assert.Equal(7.5, pressure.Some);
        Assert.Null(pressure.Full);
    }

    [Fact]
    public void TheReader_ReadsCpuIoAndMemoryFromTheDirectory()
    {
        Write("cpu", "some avg10=11.11 avg60=0 avg300=0 total=1\n");
        Write("io", "some avg10=22.22 avg60=0 avg300=0 total=1\nfull avg10=33.33 avg60=0 avg300=0 total=1\n");
        Write("memory", "some avg10=0.00 avg60=0 avg300=0 total=1\nfull avg10=0.00 avg60=0 avg300=0 total=1\n");

        var snapshot = new HostPressureReader(_directory).Read();

        Assert.Equal(11.11, snapshot.Cpu.Some);
        Assert.Equal(22.22, snapshot.Io.Some);
        Assert.Equal(33.33, snapshot.Io.Full);
        Assert.Equal(0.0, snapshot.Memory.Some);
    }

    [Fact]
    public void TheReader_ResolvesFromTheContainer_WithTheKernelDirectory()
    {
        var services = new ServiceCollection();
        services.AddSlowRequestDiagnostics(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var reader = provider.GetRequiredService<HostPressureReader>();

        // Windows dev machines have no /proc/pressure: the answer is "unknown", never an exception.
        Assert.NotNull(reader.Read());
    }

    [Fact]
    public void AMissingDirectory_IsNotAvailable_AndDoesNotThrow()
    {
        var snapshot = new HostPressureReader(Path.Combine(_directory, "does-not-exist")).Read();

        Assert.Equal(HostPressureSnapshot.Unavailable, snapshot);
    }

    [Fact]
    public void AMissingFile_IsNotAvailable_WhileTheOthersAreStillRead()
    {
        Write("cpu", "some avg10=4.00 avg60=0 avg300=0 total=1\n");

        var snapshot = new HostPressureReader(_directory).Read();

        Assert.Equal(4.0, snapshot.Cpu.Some);
        Assert.Equal(ResourcePressure.Unavailable, snapshot.Io);
        Assert.Equal(ResourcePressure.Unavailable, snapshot.Memory);
    }

    [Fact]
    public void AnUnreadableEntry_IsNotAvailable_AndDoesNotThrow()
    {
        // A directory where the "cpu" file should be: File.Exists is false, the read must not fail.
        Directory.CreateDirectory(Path.Combine(_directory, "cpu"));

        var snapshot = new HostPressureReader(_directory).Read();

        Assert.Equal(ResourcePressure.Unavailable, snapshot.Cpu);
    }

    [Theory]
    [InlineData(12.5, "12.5")]
    [InlineData(0.0, "0")]
    [InlineData(3.456, "3.46")]
    [InlineData(null, "n/a")]
    public void Format_UsesTheInvariantCulture_OrNotAvailable(double? value, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
        try
        {
            Assert.Equal(expected, HostPressureSnapshot.Format(value));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
