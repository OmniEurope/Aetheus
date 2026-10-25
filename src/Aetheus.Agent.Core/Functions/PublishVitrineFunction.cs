// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>publish-vitrine</c> function, port of <c>publish-vitrine.sh</c> (the
/// <c>PUBLISH_VITRINE_PROGRAM</c> heredoc of the production pipeline): publishes a static site into a
/// dedicated web root. It refuses before touching anything:
/// <list type="number">
/// <item>a domain that is empty or still names <c>SITE_DOMAIN</c> (the Variable Library did not provide it);</item>
/// <item>a web root that is not exactly one directory under <c>/var/www</c> without <c>..</c>, then one that
/// is not once its symbolic links are resolved (<c>readlink -m</c>), then one that is a symbolic link;</item>
/// <item>a source that is missing or a symbolic link;</item>
/// <item>a source that still holds the placeholder after the domain was written over it in every
/// <c>.html</c>, <c>.xml</c>, <c>.txt</c> and <c>.json</c> file.</item>
/// </list>
/// It then stages the site next to the live files and swaps it in: the live files are copied to a backup,
/// the web root is emptied and the staged files moved in. A failure after the swap began puts the backup
/// back; the stage and the backup never outlive the call.
///
/// Same sentences as the script. Differences, deliberate: the script's positional arguments are the
/// <c>domain</c>, <c>web_root</c> and <c>source_dir</c> parameters (the last relative to the workspace,
/// default <c>site</c>) and the placeholder it hard-coded, <c>aetheus.example</c>, is the required
/// <c>placeholder</c> parameter; the domain is written literally (<c>sed</c> read <c>&amp;</c>, <c>\</c> and
/// <c>#</c> in it as syntax); the placeholder scan reads regular files only and does not follow links
/// (<c>grep -R</c> followed them out of the site); a refusal is the failure reason, and the scan lists
/// at most 50 matches.
/// </summary>
public sealed class PublishVitrineFunction : IPipelineFunction
{
    private const string WebBase = "/var/www/";
    private const int MaximumListedMatches = 50;
    private static readonly string[] s_textExtensions = [".html", ".xml", ".txt", ".json"];

    private readonly string _webBase;
    private readonly Action<string> _checkpoint;

    public PublishVitrineFunction()
        : this(WebBase, _ => { })
    {
    }

    /// <summary>Tests stand a writable directory in for <c>/var/www</c> and may fail the swap at a named
    /// point (<c>snapshot</c>, <c>clear</c>, <c>move</c>).</summary>
    internal PublishVitrineFunction(string webBase, Action<string> checkpoint)
    {
        _webBase = webBase;
        _checkpoint = checkpoint;
    }

    public string Name => PipelineFunctionCatalog.PublishVitrine.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var domain = context.Parameters["domain"];
        var givenRoot = context.Parameters["web_root"];
        var placeholder = context.Parameters["placeholder"];
        var givenSource = context.Parameters.GetValueOrDefault("source_dir") is { Length: > 0 } custom ? custom : "site";
        var sourcePath = context.Workspace is null || Path.IsPathRooted(givenSource) ? givenSource : Path.Combine(context.Workspace, givenSource);

        var refusal = DomainRefusal(domain) ?? WebRootRefusal(givenRoot) ?? SourceRefusal(givenSource, sourcePath);
        if (refusal is not null)
            return PipelineFunctionResult.Failure(refusal);

        var canonicalSource = PathCanonicalizer.ReadlinkM(sourcePath);
        await WritePlaceholderAsync(canonicalSource, placeholder, domain, cancellationToken).ConfigureAwait(false);
        var matches = await PlaceholderMatchesAsync(canonicalSource, placeholder, cancellationToken).ConfigureAwait(false);
        if (matches.Count > 0)
        {
            foreach (var match in matches.Take(MaximumListedMatches))
                await context.Log(match).ConfigureAwait(false);
            return PipelineFunctionResult.Failure($"Refusing to publish a vitrine containing unresolved {placeholder} placeholders.");
        }
        await context.Log($"Remaining {placeholder} occurrences: 0").ConfigureAwait(false);

        var webRoot = PathCanonicalizer.ReadlinkM(givenRoot);
        if (!IsWritable(webRoot))
        {
            await context.Log($"Re-run the agent installer with the deployment module and --web-root {webRoot}.", TaskLogLevel.Error).ConfigureAwait(false);
            return PipelineFunctionResult.Failure($"Web root {webRoot} is not writable by the agent.");
        }

        Publish(canonicalSource, webRoot);
        await context.Log($"Vitrine published to {webRoot} (world-readable for Apache).").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    private static string? DomainRefusal(string domain) =>
        domain.Length == 0 || domain.Contains("SITE_DOMAIN", StringComparison.Ordinal)
            ? "SITE_DOMAIN not provided by the Variable Library"
            : null;

    /// <summary>The web root must be one directory directly under the base, with no traversal; once its
    /// links are resolved it must still be, and it must not itself be a link.</summary>
    private string? WebRootRefusal(string webRoot)
    {
        if (webRoot.Contains("..", StringComparison.Ordinal) || !IsDirectChild(webRoot))
            return $"Refusing publication outside a dedicated /var/www child: '{webRoot}'";
        if (!IsDirectChild(PathCanonicalizer.ReadlinkM(webRoot)))
            return $"Refusing publication through an unsafe or escaping web root: '{webRoot}'";
        return ProductionStatePaths.IsSymbolicLink(webRoot)
            ? $"Refusing publication through a symbolic-link web root: '{webRoot}'"
            : null;
    }

    private bool IsDirectChild(string path) =>
        path.StartsWith(_webBase, StringComparison.Ordinal)
        && path.Length > _webBase.Length
        && path.IndexOf('/', _webBase.Length) < 0;

    private static string? SourceRefusal(string given, string path) =>
        !Directory.Exists(PathCanonicalizer.ReadlinkM(path)) || ProductionStatePaths.IsSymbolicLink(path)
            ? $"Refusing a missing or symbolic-link vitrine source: '{given}'"
            : null;

    /// <summary>Every text file of the source is rewritten, not a fixed list of pages: a hard-coded list
    /// would silently leave placeholders in the pages it does not name.</summary>
    private static async Task WritePlaceholderAsync(string source, string placeholder, string domain, CancellationToken cancellationToken)
    {
        var find = Encoding.UTF8.GetBytes(placeholder);
        var replacement = Encoding.UTF8.GetBytes(domain);
        foreach (var file in DirectoryTree.Files(source).Where(file => s_textExtensions.Any(extension => file.Name.EndsWith(extension, StringComparison.Ordinal))))
        {
            var content = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            if (content.AsSpan().IndexOf(find) < 0)
                continue;
            await File.WriteAllBytesAsync(file.FullName, Replace(content, find, replacement), cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] Replace(byte[] content, byte[] find, byte[] replacement)
    {
        var result = new List<byte>(content.Length);
        var rest = content.AsSpan();
        int at;
        while ((at = rest.IndexOf(find)) >= 0)
        {
            result.AddRange(rest[..at]);
            result.AddRange(replacement);
            rest = rest[(at + find.Length)..];
        }
        result.AddRange(rest);
        return [.. result];
    }

    /// <summary><c>grep -R -n</c> of the placeholder over every file of the source, as lines
    /// <c>path:line:text</c>, or a binary file's name.</summary>
    private static async Task<List<string>> PlaceholderMatchesAsync(string source, string placeholder, CancellationToken cancellationToken)
    {
        var find = Encoding.UTF8.GetBytes(placeholder);
        var matches = new List<string>();
        foreach (var file in DirectoryTree.Files(source))
        {
            var content = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            if (content.AsSpan().IndexOf(find) < 0)
                continue;
            if (content.AsSpan().IndexOf((byte)0) >= 0)
            {
                matches.Add($"Binary file {file.FullName} matches");
                continue;
            }
            var lines = Encoding.UTF8.GetString(content).Split('\n');
            matches.AddRange(lines
                .Select((text, index) => (text, number: index + 1))
                .Where(line => line.text.Contains(placeholder, StringComparison.Ordinal))
                .Select(line => $"{file.FullName}:{line.number}:{line.text}"));
        }
        return matches;
    }

    /// <summary><c>mkdir -p "$WEB_ROOT"; [ -w "$WEB_ROOT" ]</c>: whether the web root exists, or can be
    /// made, and the agent can create something in it.</summary>
    private static bool IsWritable(string webRoot)
    {
        var probe = $"{webRoot}/.aetheus-probe.{Environment.ProcessId}";
        try
        {
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Stage the site beside the live files, copy the live files aside, empty the web root and
    /// move the stage in; a failure once the swap began puts the live files back.</summary>
    private void Publish(string source, string webRoot)
    {
        var stage = $"{webRoot}/.aetheus-next.{Environment.ProcessId}";
        var backup = $"{webRoot}/.aetheus-previous.{Environment.ProcessId}";
        var keep = new[] { Path.GetFileName(stage), Path.GetFileName(backup) };
        DirectoryTree.RemoveTree(stage);
        DirectoryTree.RemoveTree(backup);
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(backup);
        var switched = false;
        try
        {
            DirectoryTree.CopyContents(source, stage);
            DirectoryTree.GrantReadToAll(stage);
            _checkpoint("snapshot");
            DirectoryTree.CopyContents(webRoot, backup, name => keep.Contains(name, StringComparer.Ordinal));
            switched = true;
            _checkpoint("clear");
            DirectoryTree.RemoveContents(webRoot, keep);
            _checkpoint("move");
            DirectoryTree.MoveContents(stage, webRoot);
            switched = false;
        }
        catch (Exception) when (switched)
        {
            DirectoryTree.RemoveContents(webRoot, keep[1]);
            DirectoryTree.MoveContents(backup, webRoot);
            throw;
        }
        finally
        {
            DirectoryTree.RemoveTree(stage);
            DirectoryTree.RemoveTree(backup);
        }
    }
}
