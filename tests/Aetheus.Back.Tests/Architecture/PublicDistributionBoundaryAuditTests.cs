// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Scans exportable text, including overlays, against the public-distribution forbidden
/// literals with the exporter’s matching semantics. Dropped test bodies remain in this stricter
/// scan; the guard itself is excluded from public distribution.</summary>
public sealed class PublicDistributionBoundaryAuditTests
{
    private const string BoundaryPath = "deploy/public-distribution/boundary.json";
    private const string Overlay = "deploy/public-distribution/overlay/";

    [Fact]
    public void TheMatcher_NamesTheFileAndTheLiteral()
    {
        var hits = FindForbidden("tests/Some.Tests/ServerTest.cs", "var host = \"10.9.8.7\";", ["10.9.8.7", "absent.example"]);

        Assert.Equal(["tests/Some.Tests/ServerTest.cs: 10.9.8.7"], hits);
        Assert.Empty(FindForbidden("a.cs", "nothing here", ["10.9.8.7"]));
    }

    internal static List<string> FindForbidden(string path, string content, IEnumerable<string> forbidden)
        => forbidden.Where(literal => content.Contains(literal, StringComparison.Ordinal))
            .Select(literal => $"{path}: {literal}").ToList();

    /// <summary>(source path, path in the distribution) for each file the export copies; the overlay replaces the source of the same path.</summary>
    internal static List<(string Source, string Target)> PublishedFiles(string lsFilesOutput, Boundary boundary)
    {
        var files = lsFilesOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var exclude = boundary.Exclude.Select(GlobToRegex).ToList();
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.StartsWith(Overlay, StringComparison.Ordinal)) continue;
            if (exclude.Any(regex => regex.IsMatch(file))) continue;
            copies[boundary.Rename.GetValueOrDefault(file, file)] = file;
        }

        foreach (var file in files.Where(file => file.StartsWith(Overlay, StringComparison.Ordinal)))
            copies[file[Overlay.Length..]] = file;
        return copies.Select(pair => (Source: pair.Value, Target: pair.Key)).ToList();
    }

    // Mirror of globToRegExp in export-public-distribution.mjs.
    internal static Regex GlobToRegex(string glob)
    {
        var pattern = glob.Trim();
        var anyDirectory = pattern.StartsWith("**/", StringComparison.Ordinal);
        if (anyDirectory) pattern = pattern[3..];
        var body = string.Join(".*", pattern.Split("**").Select(part =>
            Regex.Replace(Regex.Replace(part, @"[.+^${}()|\[\]\\]", @"\$&"), @"\*", "[^/]*")));
        return new Regex($"^{(anyDirectory ? "(?:.*/)?" : "")}{body}$", RegexOptions.CultureInvariant);
    }

    private static bool IsText(byte[] bytes) => !bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0);

    private static Boundary LoadBoundary(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, BoundaryPath)));
        var element = document.RootElement;
        return new Boundary(
            Strings(element, "exclude"),
            Strings(element, "forbidden"),
            element.GetProperty("rename").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal));
    }

    private static List<string> Strings(JsonElement element, string name)
        => element.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToList();

    internal sealed record Boundary(List<string> Exclude, List<string> Forbidden, Dictionary<string, string> Rename);

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"safe.directory={workingDir.Replace('\\', '/')}");
        foreach (var a in args) psi.ArgumentList.Add(a);
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start git - it must be on PATH on the build host.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            p.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("git ls-files did not complete within 60 seconds.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {p.ExitCode}: {stderr.Trim()}");
        return stdout;
    }
}
