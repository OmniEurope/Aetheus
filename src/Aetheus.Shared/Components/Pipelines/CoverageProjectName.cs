// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Groups coverage by <see cref="CoverageFileDto.Assembly"/> when available, otherwise by
/// the directory after the last src or tests segment, the first path directory, or / for a root
/// file. Sharing this fallback keeps API and page groupings consistent.</summary>
public static class CoverageProjectName
{
    public const string Root = "/";

    private static readonly char[] Separators = ['/', '\\'];

    public static string Of(CoverageFileDto file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!string.IsNullOrWhiteSpace(file.Assembly)) return file.Assembly;

        var segments = file.File.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var directories = segments.Length - 1;
        if (directories <= 0) return Root;

        for (var index = directories - 2; index >= 0; index--)
        {
            if (IsSourceRoot(segments[index])) return segments[index + 1];
        }

        return segments[0];
    }

    private static bool IsSourceRoot(string segment) =>
        segment.Equals("src", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("test", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("tests", StringComparison.OrdinalIgnoreCase);
}
