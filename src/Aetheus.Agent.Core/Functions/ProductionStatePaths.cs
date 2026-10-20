// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The path rules the production deploy scripts apply to their state directory, with the shell's
/// meaning: <c>dirname</c>, the <c>case</c> that bounds the directory to a direct child of
/// <c>/var/lib</c>, <c>[ -L ]</c>, and the dated recovery copies <c>&lt;file&gt;-YYYYMMDDTHHMMSSZ</c>
/// that <c>find -name "$PREFIX-????????T??????Z"</c> selects.
/// </summary>
internal static partial class ProductionStatePaths
{
    [GeneratedRegex("^.{8}T.{6}Z$", RegexOptions.Singleline)]
    private static partial Regex DatedSuffix();

    /// <summary>POSIX <c>dirname</c>: trailing slashes ignored, <c>.</c> for a bare name, <c>/</c> for
    /// the root.</summary>
    public static string Dirname(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
            return path.Length == 0 ? "." : "/";
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0)
            return ".";
        var parent = trimmed[..slash].TrimEnd('/');
        return parent.Length == 0 ? "/" : parent;
    }

    /// <summary>Where production state directories live.</summary>
    public const string StateRoot = "/var/lib/";

    /// <summary>The refusal of <c>prod-deploy-prepare.sh</c> for a state directory that is not a real,
    /// direct child of <paramref name="root"/> (<see cref="StateRoot"/> outside tests, which need a
    /// writable stand-in), or null when its shape is accepted.</summary>
    public static string? ShapeRefusal(string stateDirectory, string root = StateRoot)
    {
        var name = root.TrimEnd('/');
        if (stateDirectory.Contains("..", StringComparison.Ordinal))
            return $"Production state directory contains a traversal: {stateDirectory}";
        if (!stateDirectory.StartsWith(root, StringComparison.Ordinal) || stateDirectory.Length == root.Length)
            return $"Production state directory must live under {name}: {stateDirectory}";
        return stateDirectory.IndexOf('/', root.Length) >= 0
            ? $"Production state directory must be a direct child of {name}: {stateDirectory}"
            : null;
    }

    /// <summary><c>[ -L path ]</c>.</summary>
    public static bool IsSymbolicLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && info.LinkTarget is not null;
    }

    /// <summary>Whether a regular dated copy of <paramref name="envFile"/> exists next to it.</summary>
    public static bool HasDatedBackup(string envFile)
    {
        var directory = Dirname(envFile);
        var prefix = Path.GetFileName(envFile.TrimEnd('/')) + "-";
        return Directory.Exists(directory)
            && Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Any(name => name!.StartsWith(prefix, StringComparison.Ordinal)
                    && DatedSuffix().IsMatch(name[prefix.Length..])
                    && !IsSymbolicLink(Path.Combine(directory, name)));
    }
}
