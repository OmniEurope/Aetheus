// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Shared.Components.Servers;

/// <summary>
/// R-41: reads <c>/etc/os-release</c> (freedesktop.org os-release(5)). One <c>KEY=value</c> per line; a value may be
/// quoted with double or single quotes, a double-quoted one may escape <c>"</c>, <c>\</c>, <c>$</c> and the backquote;
/// blank lines and lines starting with <c>#</c> are comments. A line that is not an assignment is skipped, never guessed.
/// Here, beside the DTO, so the agent that reads the file and the tests on any platform share one reader.
/// </summary>
public static class OsReleaseFile
{
    /// <summary>The usual path, then the fallback os-release(5) names.</summary>
    public static IReadOnlyList<string> Paths { get; } = ["/etc/os-release", "/usr/lib/os-release"];

    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0) continue;
            var key = line[..equals];
            if (!key.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')) continue;
            if (Unquote(line[(equals + 1)..]) is { } value) values[key] = value;
        }

        return values;
    }

    /// <summary>The report a parsed file gives, or null when it has no <c>ID</c>: without it no release can be named.</summary>
    public static (string Id, string? VersionId, string? Version, string? PrettyName)? Identify(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.TryGetValue("ID", out var id) || string.IsNullOrWhiteSpace(id)) return null;
        return (id.Trim().ToLowerInvariant(), Optional(values, "VERSION_ID"), Optional(values, "VERSION"), Optional(values, "PRETTY_NAME"));
    }

    private static string? Optional(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string? Unquote(string value)
    {
        if (value.Length == 0) return string.Empty;
        var quote = value[0];
        if (quote is not ('"' or '\'')) return value;
        if (value.Length < 2 || value[^1] != quote) return null;
        var inner = value[1..^1];
        if (quote == '\'') return inner;
        var result = new StringBuilder(inner.Length);
        for (var index = 0; index < inner.Length; index++)
        {
            if (inner[index] == '\\' && index + 1 < inner.Length && inner[index + 1] is '"' or '\\' or '$' or '`')
                index++;
            result.Append(inner[index]);
        }

        return result.ToString();
    }
}

/// <summary>
/// R-41: the installed packages of a dpkg answer to <see cref="QueryArgv"/>: the status abbreviation, the package, its
/// version, its <c>Source</c> field and its architecture. dpkg leaves <c>Source</c> empty when the source is the package
/// itself and writes <c>name (version)</c> when the source version differs from the binary's (a binNMU, a kernel
/// meta-package). A multiarch package installed for two architectures (<c>libc6</c> amd64 and i386) is two packages.
/// A package that is not installed (<c>n</c>) or only has its configuration left (<c>c</c>, the <c>rc</c> of a remove)
/// is skipped, as by the on-demand inventory.
/// </summary>
public static class DpkgHostPackages
{
    /// <summary>The <c>dpkg-query</c> argv: shell-free, the escapes are dpkg's own. The architecture comes last, so a
    /// four-column line still reads.</summary>
    public static IReadOnlyList<string> QueryArgv { get; } = ["-W", "-f=${db:Status-Abbrev}\\t${Package}\\t${Version}\\t${Source}\\t${Architecture}\\n"];

    /// <summary>The os-release <c>ID</c>s whose packages dpkg holds and the watch reads.</summary>
    public static IReadOnlyList<string> Families { get; } = ["debian", "ubuntu"];

    public static bool IsDpkgFamily(string? osId) =>
        osId is not null && Families.Contains(osId.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    public static List<HostPackageDto> Parse(string? stdout)
    {
        var packages = new Dictionary<(string Name, string? Architecture), HostPackageDto>();
        foreach (var raw in (stdout ?? string.Empty).Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length < 3) continue;
            var state = parts[0].Trim();
            var name = parts[1].Trim();
            var version = parts[2].Trim();
            if (state.Length < 2 || state[1] is 'n' or 'c' || name.Length == 0 || version.Length == 0) continue;
            if (name.Length > HostPackageLimits.MaxNameLength || version.Length > HostPackageLimits.MaxVersionLength) continue;
            var (sourceName, sourceVersion) = Source(parts.Length > 3 ? parts[3] : string.Empty);
            var architecture = Architecture(parts.Length > 4 ? parts[4] : string.Empty);
            packages[(name, architecture)] = new HostPackageDto
            {
                Name = name,
                Version = version,
                Architecture = architecture,
                SourceName = sourceName,
                SourceVersion = sourceVersion
            };
        }

        return [.. packages.Values
            .OrderBy(package => package.Name, StringComparer.Ordinal)
            .ThenBy(package => package.Architecture, StringComparer.Ordinal)];
    }

    /// <summary><c>amd64</c>, <c>all</c>, <c>armhf</c>: lower-case letters, digits and dashes, bounded; anything else is
    /// left out rather than stored.</summary>
    private static string? Architecture(string field)
    {
        var value = field.Trim();
        return value.Length is > 0 and <= HostPackageLimits.MaxArchitectureLength
               && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            ? value
            : null;
    }

    /// <summary><c>openssl</c>, <c>linux-signed (6.8.0-45.45)</c>, or empty.</summary>
    private static (string? Name, string? Version) Source(string field)
    {
        var value = field.Trim();
        if (value.Length == 0) return (null, null);
        var open = value.IndexOf(" (", StringComparison.Ordinal);
        if (open < 0) return (Bounded(value, HostPackageLimits.MaxNameLength), null);
        var name = value[..open].Trim();
        var version = value.EndsWith(')') ? value[(open + 2)..^1].Trim() : null;
        return (Bounded(name, HostPackageLimits.MaxNameLength), Bounded(version, HostPackageLimits.MaxVersionLength));
    }

    private static string? Bounded(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length > max ? null : value;
}
