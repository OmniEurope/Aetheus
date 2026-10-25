// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.Versioning;
using Aetheus.Agent.Core.Functions;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>publish-vitrine.sh</c>. The deleted
/// <c>DeploymentSafetyAuditTests</c> assertions about it read its text: the destination must be a direct
/// child of <c>/var/www</c> without traversal, the web root is canonicalised (<c>readlink -m</c>) and a
/// symbolic-link web root is refused, a vitrine with unresolved placeholders is refused, and the
/// placeholder check must not be neutralised by <c>|| true</c>. Each is exercised here on real
/// directories, a writable one standing in for <c>/var/www</c>, along with the swap and its rollback.
/// </summary>
public sealed class PublishVitrineFunctionTests : ProductionFunctionTestBase
{
    private const string Placeholder = "aetheus.example";
    private const string Domain = "www.aetheus.org";

    private readonly string _base;
    private readonly string _webRoot;
    private readonly string _source;

    public PublishVitrineFunctionTests()
        : base("publish-vitrine")
    {
        _base = PathIn("www") + "/";
        Directory.CreateDirectory(_base);
        _webRoot = _base + "aetheus-site";
        _source = Path.Combine(Workspace, "site");
        Directory.CreateDirectory(_source);
        File.WriteAllText(Path.Combine(_source, "index.html"), $"<a href=\"https://{Placeholder}/\">{Placeholder}</a>\n");
        File.WriteAllText(Path.Combine(_source, "sitemap.xml"), $"<loc>https://{Placeholder}/</loc>");
        File.WriteAllText(Path.Combine(_source, "robots.txt"), $"Sitemap: https://{Placeholder}/sitemap.xml\n");
        Directory.CreateDirectory(Path.Combine(_source, "assets"));
        File.WriteAllText(Path.Combine(_source, "assets", "site.json"), $"{{\"host\":\"{Placeholder}\"}}");
        File.WriteAllText(Path.Combine(_source, "assets", "style.css"), "body{}");
        Parameters["domain"] = Domain;
        Parameters["web_root"] = _webRoot;
        Parameters["placeholder"] = Placeholder;
    }

    private PublishVitrineFunction Function(Action<string>? checkpoint = null) => new(_base, checkpoint ?? (_ => { }));

    private string[] WebRootEntries() =>
        Directory.GetFileSystemEntries(_webRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal).Select(name => name!).ToArray();

    private void LiveSite()
    {
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "the live site\n");
        File.WriteAllText(Path.Combine(_webRoot, ".htaccess"), "live dotfile\n");
        Directory.CreateDirectory(Path.Combine(_webRoot, "old"));
        File.WriteAllText(Path.Combine(_webRoot, "old", "page.html"), "an old page\n");
    }

    [Fact]
    public async Task TheSite_IsPublishedWithTheDomainWrittenOverThePlaceholderInEveryTextFile()
    {
        await SucceedAsync(Function());

        Assert.Equal($"<a href=\"https://{Domain}/\">{Domain}</a>\n", Read(Path.Combine(_webRoot, "index.html")));
        Assert.Equal($"<loc>https://{Domain}/</loc>", Read(Path.Combine(_webRoot, "sitemap.xml")));
        Assert.Equal($"Sitemap: https://{Domain}/sitemap.xml\n", Read(Path.Combine(_webRoot, "robots.txt")));
        Assert.Equal($"{{\"host\":\"{Domain}\"}}", Read(Path.Combine(_webRoot, "assets", "site.json")));
        Assert.Equal("body{}", Read(Path.Combine(_webRoot, "assets", "style.css")));
        Assert.Equal(["assets", "index.html", "robots.txt", "sitemap.xml"], WebRootEntries());
        Assert.Contains($"Remaining {Placeholder} occurrences: 0", LogLines);
        Assert.Contains($"Vitrine published to {_webRoot} (world-readable for Apache).", LogLines);
    }

    [Fact]
    public async Task ThePreviousSite_IsReplacedWhole_DotfilesIncluded_AndNoStageOrBackupRemains()
    {
        LiveSite();

        await SucceedAsync(Function());

        Assert.Equal(["assets", "index.html", "robots.txt", "sitemap.xml"], WebRootEntries());
        Assert.DoesNotContain("live", Read(Path.Combine(_webRoot, "index.html")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWebRootThatDoesNotExistYet_IsCreated()
    {
        await SucceedAsync(Function());

        Assert.True(Directory.Exists(_webRoot));
    }

    [Fact]
    public async Task TheSourceItself_IsNotModifiedBeyondThePlaceholder_AndTheDomainIsWrittenLiterally()
    {
        Parameters["domain"] = "a&b.example.org";

        await SucceedAsync(Function());

        Assert.Equal("<a href=\"https://a&b.example.org/\">a&b.example.org</a>\n", Read(Path.Combine(_webRoot, "index.html")));
    }

    [Fact]
    public async Task ASourceDirectoryNamedByTheStep_IsPublished()
    {
        var docs = Directory.CreateDirectory(Path.Combine(Workspace, ".delivery-vitrine", "docs-site", "public")).FullName;
        File.WriteAllText(Path.Combine(docs, "index.html"), $"<p>{Placeholder}</p>");
        Parameters["source_dir"] = ".delivery-vitrine/docs-site/public";

        await SucceedAsync(Function());

        Assert.Equal($"<p>{Domain}</p>", Read(Path.Combine(_webRoot, "index.html")));
        Assert.Equal(["index.html"], WebRootEntries());
    }

    [Theory]
    [InlineData("SITE_DOMAIN")]
    [InlineData("www.SITE_DOMAIN.org")]
    public async Task ADomainTheVariableLibraryDidNotProvide_IsRefused(string domain)
    {
        Parameters["domain"] = domain;

        Assert.Equal("SITE_DOMAIN not provided by the Variable Library", await FailureAsync(Function()));

        Assert.False(Directory.Exists(_webRoot));
    }

    [Theory]
    [InlineData("aetheus-site/nested")]
    [InlineData("aetheus-site/")]
    [InlineData("../escape")]
    [InlineData("aetheus-site/../../escape")]
    [InlineData("a/../b")]
    [InlineData("")]
    public async Task AWebRootThatIsNotOneDirectoryDirectlyUnderTheBase_IsRefused(string relative)
    {
        var webRoot = _base + relative;
        Parameters["web_root"] = webRoot;

        Assert.Equal($"Refusing publication outside a dedicated /var/www child: '{webRoot}'", await FailureAsync(Function()));

        Assert.Empty(Directory.GetFileSystemEntries(_base));
    }

    [Fact]
    public async Task AWebRootOutsideTheBase_IsRefused()
    {
        var outside = PathIn("elsewhere");
        Parameters["web_root"] = outside;

        Assert.Equal($"Refusing publication outside a dedicated /var/www child: '{outside}'", await FailureAsync(Function()));

        Assert.False(Directory.Exists(outside));
    }

    [Fact]
    public async Task AWebRootThatIsASymbolicLinkToAnotherDirectoryOfTheBase_IsRefusedAsASymbolicLink()
    {
        var real = Directory.CreateDirectory(_base + "real-site").FullName;
        File.WriteAllText(Path.Combine(real, "index.html"), "untouched\n");
        DirectoryLinks.Create(_webRoot, real);

        Assert.Equal($"Refusing publication through a symbolic-link web root: '{_webRoot}'", await FailureAsync(Function()));

        Assert.Equal("untouched\n", Read(Path.Combine(real, "index.html")));
        Assert.Equal(["index.html"], Directory.GetFileSystemEntries(real).Select(Path.GetFileName));
        DirectoryLinks.Remove(_webRoot);
    }

    [Fact]
    public async Task AWebRootThatIsASymbolicLinkOutOfTheBase_IsRefusedAsEscaping()
    {
        var outside = Directory.CreateDirectory(PathIn("elsewhere")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "do not delete\n");
        DirectoryLinks.Create(_webRoot, outside);

        Assert.Equal($"Refusing publication through an unsafe or escaping web root: '{_webRoot}'", await FailureAsync(Function()));

        Assert.Equal(["precious.txt"], Directory.GetFileSystemEntries(outside).Select(Path.GetFileName));
        DirectoryLinks.Remove(_webRoot);
    }

    [Fact]
    public async Task AWebRootBehindALinkedParent_IsRefusedAsEscaping()
    {
        var outside = Directory.CreateDirectory(PathIn("elsewhere")).FullName;
        DirectoryLinks.Create(_base + "hop", outside);
        Parameters["web_root"] = _base + "hop";

        Assert.Equal($"Refusing publication through an unsafe or escaping web root: '{_base}hop'", await FailureAsync(Function()));

        DirectoryLinks.Remove(_base + "hop");
    }

    [Fact]
    public async Task AMissingSource_IsRefused()
    {
        Directory.Delete(_source, recursive: true);

        Assert.Equal("Refusing a missing or symbolic-link vitrine source: 'site'", await FailureAsync(Function()));

        Assert.False(Directory.Exists(_webRoot));
    }

    [Fact]
    public async Task ASourceThatIsASymbolicLink_IsRefused()
    {
        var real = Directory.CreateDirectory(PathIn("real-source")).FullName;
        File.WriteAllText(Path.Combine(real, "index.html"), Placeholder);
        Directory.Delete(_source, recursive: true);
        DirectoryLinks.Create(_source, real);

        Assert.Equal("Refusing a missing or symbolic-link vitrine source: 'site'", await FailureAsync(Function()));

        Assert.Equal(Placeholder, Read(Path.Combine(real, "index.html")));
        DirectoryLinks.Remove(_source);
    }

    [Fact]
    public async Task APlaceholderInAFileTheDomainIsNotWrittenOver_IsRefused_AndTheLiveSiteIsKept()
    {
        LiveSite();
        File.WriteAllText(Path.Combine(_source, "assets", "app.js"), $"const host = '{Placeholder}';\n");

        Assert.Equal($"Refusing to publish a vitrine containing unresolved {Placeholder} placeholders.", await FailureAsync(Function()));

        Assert.Equal("the live site\n", Read(Path.Combine(_webRoot, "index.html")));
        Assert.Contains(Harness.Log, entry => entry.Line.EndsWith("app.js:1:const host = 'aetheus.example';", StringComparison.Ordinal));
        Assert.DoesNotContain($"Remaining {Placeholder} occurrences: 0", LogLines);
    }

    [Fact]
    public async Task APlaceholderInABinaryFile_IsRefused()
    {
        File.WriteAllBytes(Path.Combine(_source, "logo.bin"), [0, 1, .. System.Text.Encoding.UTF8.GetBytes(Placeholder), 2]);

        Assert.Equal($"Refusing to publish a vitrine containing unresolved {Placeholder} placeholders.", await FailureAsync(Function()));

        Assert.Contains(Harness.Log, entry => entry.Line.StartsWith("Binary file ", StringComparison.Ordinal) && entry.Line.EndsWith("logo.bin matches", StringComparison.Ordinal));
        Assert.False(Directory.Exists(_webRoot));
    }

    [Fact]
    public async Task ADomainThatContainsThePlaceholderItself_LeavesItUnresolvedAndIsRefused()
    {
        Parameters["domain"] = $"www.{Placeholder}";

        Assert.Equal($"Refusing to publish a vitrine containing unresolved {Placeholder} placeholders.", await FailureAsync(Function()));
    }

    [Fact]
    public async Task AWebRootThatCannotBeWritten_IsRefusedWithTheInstallerHint()
    {
        File.WriteAllText(_webRoot, "a file, not a directory\n");

        Assert.Equal($"Web root {_webRoot} is not writable by the agent.", await FailureAsync(Function()));

        Assert.Contains(Harness.Log, entry => entry.Level == TaskLogLevel.Error
            && entry.Line == $"Re-run the agent installer with the deployment module and --web-root {_webRoot}.");
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("clear")]
    [InlineData("move")]
    public async Task AFailureDuringTheSwap_PutsTheLiveSiteBack_AndLeavesNoStageOrBackup(string failAt)
    {
        LiveSite();
        var function = Function(point =>
        {
            if (point == failAt)
                throw new IOException("injected failure at " + point);
        });

        var (result, _) = await RunAsync(function);

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains("injected failure at " + failAt, result.FailureReason, StringComparison.Ordinal);
        Assert.Equal([".htaccess", "index.html", "old"], WebRootEntries());
        Assert.Equal("the live site\n", Read(Path.Combine(_webRoot, "index.html")));
        Assert.Equal("live dotfile\n", Read(Path.Combine(_webRoot, ".htaccess")));
        Assert.Equal("an old page\n", Read(Path.Combine(_webRoot, "old", "page.html")));
    }

    [PlatformFact("linux")]
    [SupportedOSPlatform("linux")]
    public async Task ThePublishedFiles_AreReadableByEveryone()
    {
        File.SetUnixFileMode(Path.Combine(_source, "index.html"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(Path.Combine(_source, "assets"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await SucceedAsync(Function());

        var everyoneReads = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        Assert.Equal(everyoneReads, File.GetUnixFileMode(Path.Combine(_webRoot, "index.html")) & everyoneReads);
        var directory = File.GetUnixFileMode(Path.Combine(_webRoot, "assets"));
        Assert.Equal(everyoneReads | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
            directory & (everyoneReads | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
    }

    [PlatformFact("linux")]
    [SupportedOSPlatform("linux")]
    public async Task ALinkInsideTheSite_IsPublishedAsALink_NeverFollowed()
    {
        var outside = Harness.Write("outside/secret.txt", "outside the site\n");
        File.CreateSymbolicLink(Path.Combine(_source, "link.txt"), outside);

        await SucceedAsync(Function());

        Assert.Equal(outside, new FileInfo(Path.Combine(_webRoot, "link.txt")).LinkTarget);
    }
}
