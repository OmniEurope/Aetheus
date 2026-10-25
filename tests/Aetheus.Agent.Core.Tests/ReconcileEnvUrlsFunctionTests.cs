// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.Versioning;
using Aetheus.Agent.Core.Functions;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>reconcile-env-urls.sh</c>, which rewrites lines of the production secret-zero
/// file. What it must never do is touch anything but the URL lines it is given, or leave the file
/// half-written. The four tests of <c>tests/js/deploy/reconcile-env-urls.test.mjs</c> are here on the
/// function: a renamed host rewrites that line only after a dated copy; nothing to change leaves the file
/// and makes no copy; a missing key is appended; a value that is not a bare https origin is refused and
/// the file is untouched.
/// </summary>
public sealed class ReconcileEnvUrlsFunctionTests : ProductionFunctionTestBase
{
    private const string Original =
        "APPNAME=aetheus\nDB_PASSWORD=0a1b2c\nAPI_BASE_URL=https://aetheus-api.example.org\nFRONT_URL=https://app.example.org\n";

    private readonly string _envFile;

    public ReconcileEnvUrlsFunctionTests()
        : base("reconcile-env-urls")
    {
        _envFile = Harness.Write("state/.env-prod", Original).Replace('\\', '/');
        Parameters["env_file"] = _envFile;
    }

    private ReconcileEnvUrlsFunction Function() => new(Clock);

    private string[] StateFiles() =>
        Directory.GetFiles(Path.GetDirectoryName(_envFile)!).Select(Path.GetFileName).Order(StringComparer.Ordinal).Select(name => name!).ToArray();

    [Fact]
    public async Task ARenamedHost_RewritesThatLineOnly_AfterADatedCopy()
    {
        Parameters["urls"] = "API_BASE_URL=https://api.example.org FRONT_URL=https://app.example.org";

        await SucceedAsync(Function());

        Assert.Equal(Original.Replace("https://aetheus-api.example.org", "https://api.example.org", StringComparison.Ordinal), Read(_envFile));
        var copy = Assert.Single(DatedCopies(_envFile));
        Assert.Equal(Original, Read(Path.Combine(Path.GetDirectoryName(_envFile)!, copy)));
        Assert.Equal([".env-prod", ".env-prod" + DatedSuffix], StateFiles());
        Assert.Contains(">>> API_BASE_URL: 'https://aetheus-api.example.org' -> 'https://api.example.org'", LogLines);
        Assert.Contains($">>> Rewrote API_BASE_URL in {_envFile} (previous copy: {_envFile}{DatedSuffix}).", LogLines);
    }

    [Fact]
    public async Task NothingToChange_LeavesTheFileAndMakesNoCopy()
    {
        Parameters["urls"] = "FRONT_URL=https://app.example.org";

        await SucceedAsync(Function());

        Assert.Equal(Original, Read(_envFile));
        Assert.Equal([".env-prod"], StateFiles());
        Assert.Contains(">>> Public URLs already match the libraries.", LogLines);
    }

    [Fact]
    public async Task AMissingKey_IsAppended()
    {
        File.WriteAllText(_envFile, "APPNAME=aetheus\n");
        Parameters["urls"] = "FRONT_URL=https://app.example.org";

        await SucceedAsync(Function());

        Assert.Equal("APPNAME=aetheus\nFRONT_URL=https://app.example.org\n", Read(_envFile));
        Assert.Contains(">>> FRONT_URL: 'unset' -> 'https://app.example.org'", LogLines);
    }

    [Theory]
    [InlineData("API_BASE_URL=http://api.example.org", "API_BASE_URL must be an https URL: 'http://api.example.org'")]
    [InlineData("API_BASE_URL=https://api.example.org/x", "API_BASE_URL must be https://host[:port] with nothing else: 'https://api.example.org/x'")]
    [InlineData("API_BASE_URL=https://api.example.org;rm", "API_BASE_URL must be https://host[:port] with nothing else: 'https://api.example.org;rm'")]
    [InlineData("db_password=https://x.example.org", "Not an environment key: 'db_password'")]
    [InlineData("API_BASE_URL=https://", "API_BASE_URL must be an https URL: 'https://'")]
    [InlineData("API_BASE_URL=HTTPS://api.example.org", "API_BASE_URL must be an https URL: 'HTTPS://api.example.org'")]
    [InlineData("API_BASE_URL", "API_BASE_URL must be an https URL: 'API_BASE_URL'")]
    [InlineData("=https://x.example.org", "Not an environment key: ''")]
    public async Task AValueThatIsNotABareHttpsOrigin_IsRefused_AndTheFileIsUntouched(string pair, string refusal)
    {
        Parameters["urls"] = pair;

        Assert.Equal(refusal, await FailureAsync(Function()));

        Assert.Equal(Original, Read(_envFile));
        Assert.Equal([".env-prod"], StateFiles());
    }

    [Theory]
    [InlineData("https://api.example.org:8443")]
    [InlineData("https://a-b.c-d.example.org")]
    public async Task AHostWithAPort_OrHyphens_IsAccepted(string url)
    {
        Parameters["urls"] = "API_BASE_URL=" + url;

        await SucceedAsync(Function());

        Assert.Contains($"API_BASE_URL={url}\n", Read(_envFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryPairIsValidatedBeforeAnyLineChanges()
    {
        Parameters["urls"] = "API_BASE_URL=https://api.example.org FRONT_URL=http://app.example.org";

        Assert.Equal("FRONT_URL must be an https URL: 'http://app.example.org'", await FailureAsync(Function()));

        Assert.Equal(Original, Read(_envFile));
        Assert.Equal([".env-prod"], StateFiles());
    }

    [Fact]
    public async Task EveryLineOfTheKey_IsRewritten_AndTheLastOneIsTheCurrentValue()
    {
        File.WriteAllText(_envFile, "FRONT_URL=https://old.example.org\nAPPNAME=a\nFRONT_URL=https://older.example.org\nFRONT_URL_PROD=https://keep.example.org\n");
        Parameters["urls"] = "FRONT_URL=https://app.example.org";

        await SucceedAsync(Function());

        Assert.Equal(
            "FRONT_URL=https://app.example.org\nAPPNAME=a\nFRONT_URL=https://app.example.org\nFRONT_URL_PROD=https://keep.example.org\n",
            Read(_envFile));
        Assert.Contains(">>> FRONT_URL: 'https://older.example.org' -> 'https://app.example.org'", LogLines);
    }

    [Fact]
    public async Task AFileWithoutAFinalNewline_GetsTheLineOnALineOfItsOwn()
    {
        File.WriteAllText(_envFile, "APPNAME=aetheus");
        Parameters["urls"] = "FRONT_URL=https://app.example.org";

        await SucceedAsync(Function());

        Assert.Equal("APPNAME=aetheus\nFRONT_URL=https://app.example.org\n", Read(_envFile));
    }

    [Fact]
    public async Task ADatedCopyAlreadyThere_IsNeverOverwritten()
    {
        var copy = Harness.Write("state/.env-prod" + DatedSuffix, "an earlier copy\n");
        Parameters["urls"] = "API_BASE_URL=https://api.example.org";

        await SucceedAsync(Function());

        Assert.Equal("an earlier copy\n", Read(copy));
    }

    [PlatformFact("linux")]
    [SupportedOSPlatform("linux")]
    public async Task TheRewrittenFileAndItsDatedCopy_AreReadableByTheirOwnerOnly()
    {
        Parameters["urls"] = "API_BASE_URL=https://api.example.org";

        await SucceedAsync(Function());

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(mode, File.GetUnixFileMode(_envFile));
        Assert.Equal(mode, File.GetUnixFileMode(_envFile + DatedSuffix));
    }

    [Fact]
    public async Task AMissingEnvironmentFile_IsNotARegularEnvironmentFile()
    {
        File.Delete(_envFile);
        Parameters["urls"] = "API_BASE_URL=https://api.example.org";

        Assert.Equal($"Not a regular environment file: {_envFile}", await FailureAsync(Function()));
    }

    [PlatformFact("linux")]
    [SupportedOSPlatform("linux")]
    public async Task ASymbolicLinkEnvironmentFile_IsNotARegularEnvironmentFile()
    {
        var link = PathIn("state/.env-link");
        File.CreateSymbolicLink(link, _envFile);
        Parameters["env_file"] = link;
        Parameters["urls"] = "API_BASE_URL=https://api.example.org";

        Assert.Equal($"Not a regular environment file: {link}", await FailureAsync(Function()));
        Assert.Equal(Original, Read(_envFile));
    }

    [Fact]
    public async Task NoEnvironmentFile_IsRefusedByName()
    {
        Parameters.Remove("env_file");
        Parameters["urls"] = "API_BASE_URL=https://api.example.org";

        Assert.Equal("ENV_FILE is required", await FailureAsync(Function()));
    }
}
