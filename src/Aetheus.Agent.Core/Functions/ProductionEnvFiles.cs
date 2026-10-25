// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// What the production deploy programs do to the environment file (<c>.env-prod</c>), with their
/// meaning: a line is <c>KEY=value</c>, "has a value" is <c>grep -q "^KEY=.\+"</c>, a dated recovery
/// copy is <c>cp -p file file-YYYYMMDDTHHMMSSZ</c> (mode 600, never overwritten), and a rewrite goes
/// through a mode 600 temporary file moved over the original, so a failure mid-write cannot leave the
/// file truncated.
/// </summary>
internal static class ProductionEnvFiles
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The file's lines, split on <c>\n</c>. A file that ends with a newline ends with an
    /// empty element, which <see cref="Join"/> turns back into that newline.</summary>
    public static List<string> Lines(string content) => [.. content.Split('\n')];

    /// <summary>The lines as one text, with the final newline <c>sed</c> and <c>awk</c> always
    /// leave on what they write.</summary>
    public static string Join(List<string> lines)
    {
        if (lines.Count > 0 && lines[^1].Length != 0)
            lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    /// <summary><c>grep -q "^KEY=.\+"</c>: a line that starts with <c>KEY=</c> and has at least one
    /// character after it.</summary>
    public static bool HasValue(IEnumerable<string> lines, string key) =>
        lines.Any(line => line.Length > key.Length + 1 && line.StartsWith(key + "=", StringComparison.Ordinal));

    /// <summary>Adds a line at the end of the file's lines, on a line of its own.</summary>
    public static void Append(List<string> lines, string line)
    {
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.Insert(lines.Count - 1, line);
        else
            lines.Add(line);
    }

    /// <summary><c>"$(dirname "$ENV_FILE")/$(basename "$ENV_FILE")-$(date -u +%Y%m%dT%H%M%SZ)"</c>.</summary>
    public static string DatedCopyPath(string envFile, TimeProvider time)
    {
        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return ProductionStatePaths.Dirname(envFile) + "/" + Path.GetFileName(envFile.TrimEnd('/')) + "-" + stamp;
    }

    /// <summary><c>[ -f copy ] || cp -p file copy; chmod 600 copy</c>. The copy keeps the source's modification
    /// time as <c>cp -p</c> did: Prepare host picks the latest backup by that time.</summary>
    public static void EnsureDatedCopy(string envFile, string copy)
    {
        if (!File.Exists(copy))
        {
            File.Copy(envFile, copy);
            File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(envFile));
        }
        RestrictToOwner(copy);
    }

    /// <summary><c>chmod 600</c>, where the file system keeps POSIX modes.</summary>
    public static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, OwnerReadWrite);
    }

    /// <summary>A new file readable by its owner only from the moment it exists, as the scripts' <c>umask
    /// 177</c> made it.</summary>
    public static FileStream CreateOwnerOnly(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerReadWrite;
        return new FileStream(path, options);
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="temporary"/> (mode 600) and moves it
    /// over <paramref name="envFile"/>; the temporary file does not outlive a failure.</summary>
    public static async Task ReplaceAsync(string envFile, string temporary, string content, CancellationToken cancellationToken)
    {
        try
        {
            await using (var stream = CreateOwnerOnly(temporary))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, envFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>The environment file the functions work on: the <c>env_file</c> parameter, else
    /// <c>ENV_FILE</c>.</summary>
    public static string? EnvFileInput(PipelineFunctionContext context) =>
        FunctionShell.Input(context, "env_file", "ENV_FILE");

    /// <summary><c>[ -f file ] &amp;&amp; [ ! -L file ]</c>.</summary>
    public static bool IsRegularFile(string envFile) =>
        File.Exists(envFile) && !ProductionStatePaths.IsSymbolicLink(envFile);
}
