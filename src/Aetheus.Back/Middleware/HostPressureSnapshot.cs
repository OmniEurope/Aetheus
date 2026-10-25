// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Middleware;

/// <summary>
/// The share of the last 10 seconds during which tasks of the HOST waited for a resource, as the Linux
/// pressure-stall information (PSI) reports it: <c>some</c> is the share where at least one task was
/// stalled, <c>full</c> the share where every non-idle task was (only io and memory have it). A value is
/// <see langword="null"/> when the kernel does not expose it (Windows, kernel without PSI).
/// </summary>
internal readonly record struct ResourcePressure(double? Some, double? Full)
{
    public static readonly ResourcePressure Unavailable = new(null, null);
}

/// <summary>Host-wide CPU, io and memory pressure at one instant; see <see cref="HostPressureReader"/>.</summary>
internal sealed record HostPressureSnapshot(ResourcePressure Cpu, ResourcePressure Io, ResourcePressure Memory)
{
    public static readonly HostPressureSnapshot Unavailable =
        new(ResourcePressure.Unavailable, ResourcePressure.Unavailable, ResourcePressure.Unavailable);

    /// <summary>
    /// Reads the <c>some avg10=..</c> and <c>full avg10=..</c> figures of one PSI file's content. Never
    /// throws: a missing, empty or malformed line leaves its figure <see langword="null"/>.
    /// </summary>
    public static ResourcePressure Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ResourcePressure.Unavailable;

        double? some = null;
        double? full = null;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
                continue;
            var avg10 = Avg10(fields);
            switch (fields[0])
            {
                case "some":
                    some = avg10;
                    break;
                case "full":
                    full = avg10;
                    break;
            }
        }
        return new ResourcePressure(some, full);
    }

    /// <summary>A percentage as <c>12.5</c> (invariant culture), or <c>n/a</c> when it is unknown.</summary>
    public static string Format(double? percentage) =>
        percentage is { } value ? value.ToString("0.##", CultureInfo.InvariantCulture) : "n/a";

    private static double? Avg10(string[] fields)
    {
        const string prefix = "avg10=";
        foreach (var field in fields)
        {
            if (field.StartsWith(prefix, StringComparison.Ordinal)
                && double.TryParse(field.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value))
                return value;
        }
        return null;
    }
}

/// <summary>
/// Reads <c>cpu</c>, <c>io</c> and <c>memory</c> from the PSI directory. Inside a container the files are
/// host-wide, which is what a slow-request diagnosis needs. It is called only when a slow request is
/// reported, never on the fast path, and it never throws.
/// </summary>
internal class HostPressureReader(string directory = HostPressureReader.DefaultDirectory)
{
    public const string DefaultDirectory = "/proc/pressure";

    public virtual HostPressureSnapshot Read() => new(Resource("cpu"), Resource("io"), Resource("memory"));

    private ResourcePressure Resource(string name)
    {
        try
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path)
                ? HostPressureSnapshot.Parse(File.ReadAllText(path))
                : ResourcePressure.Unavailable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ResourcePressure.Unavailable;
        }
    }
}
