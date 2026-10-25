// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Functions;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the key back-fill blocks of the production preparation program (R-464,
/// <c>GIT_RUN_TOKEN_KEY</c>, and <c>DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY</c>), which
/// <c>tests/js/deploy/prod-deploy-prepare-run-token.test.mjs</c> ran as extracted shell with an
/// <c>openssl</c> stand-in. Each test of that file has its counterpart here, on a real environment file:
/// a missing key is drawn after a dated copy and every other line is kept; it is drawn once and never
/// rotated; an empty line counts as absent and is replaced; a draw that is not 32 hexadecimal bytes
/// fails and leaves the file untouched; the draw asks for 32 bytes.
/// </summary>
public sealed class BackfillEnvSecretFunctionTests : ProductionFunctionTestBase
{
    private const string Original = "APPNAME=aetheus\nENV=prod\nENCRYPTION_KEY=00aa11bb\nDEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=cafe\n";
    private static readonly string s_firstKey = new('a', 64);
    private static readonly string s_secondKey = new('b', 64);

    private readonly List<int> _draws = [];
    private readonly string _envFile;
    private string _next = s_firstKey;

    public BackfillEnvSecretFunctionTests()
        : base("backfill-env-secret")
    {
        _envFile = Harness.Write("state/.env-prod", Original).Replace('\\', '/');
        Parameters["key"] = "GIT_RUN_TOKEN_KEY";
        Parameters["env_file"] = _envFile;
    }

    private BackfillEnvSecretFunction Function() =>
        new(Clock, bytes =>
        {
            _draws.Add(bytes);
            return _next;
        });

    private string[] StateFiles() =>
        Directory.GetFiles(Path.GetDirectoryName(_envFile)!).Select(Path.GetFileName).Order(StringComparer.Ordinal).Select(name => name!).ToArray();

    [Fact]
    public async Task AnEnvironmentWithoutTheKey_GetsOneAfterADatedCopy_EveryOtherLineKept()
    {
        await SucceedAsync(Function());

        Assert.Equal($"{Original}GIT_RUN_TOKEN_KEY={s_firstKey}\n", Read(_envFile));
        Assert.Equal([32], _draws);
        var copy = Assert.Single(DatedCopies(_envFile));
        Assert.Equal(".env-prod" + DatedSuffix, copy);
        Assert.Equal(Original, Read(Path.Combine(Path.GetDirectoryName(_envFile)!, copy)));
        Assert.Equal([".env-prod", ".env-prod" + DatedSuffix], StateFiles());
        Assert.Contains(">>> Back-filling GIT_RUN_TOKEN_KEY in " + _envFile, LogLines);
    }

    [Fact]
    public async Task TheKeyIsDrawnOnce_TheSecondPreparationKeepsItAndDrawsNothing()
    {
        await SucceedAsync(Function());
        var first = Read(_envFile);
        var files = StateFiles();
        _next = s_secondKey;

        await SucceedAsync(Function());

        Assert.Equal(first, Read(_envFile));
        Assert.Equal([32], _draws);
        Assert.Equal(files, StateFiles());
    }

    [Fact]
    public async Task AnEmptyKeyLine_CountsAsAbsent_AndIsReplacedNotDuplicated()
    {
        File.WriteAllText(_envFile, "APPNAME=aetheus\nGIT_RUN_TOKEN_KEY=\nENV=prod\n");

        await SucceedAsync(Function());

        Assert.Equal($"APPNAME=aetheus\nENV=prod\nGIT_RUN_TOKEN_KEY={s_firstKey}\n", Read(_envFile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("abababababababababababababababab")]
    [InlineData("ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB")]
    public async Task ADrawThatIsNotThirtyTwoLowercaseHexadecimalBytes_Fails_AndLeavesTheFileUntouched(string drawn)
    {
        _next = drawn;
        var expected = drawn.Length > 0 && drawn.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            ? "The random source did not draw a hexadecimal GIT_RUN_TOKEN_KEY."
            : "The random source did not draw a 32-byte GIT_RUN_TOKEN_KEY.";

        Assert.Equal(expected, await FailureAsync(Function()));

        Assert.Equal(Original, Read(_envFile));
        Assert.Equal([".env-prod"], StateFiles());
    }

    [Fact]
    public async Task TheDeploymentBootstrapIdentityKey_IsBackFilledByTheSameFunction()
    {
        File.WriteAllText(_envFile, "APPNAME=aetheus\nDEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=\n");
        Parameters["key"] = "DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY";

        await SucceedAsync(Function());

        Assert.Equal($"APPNAME=aetheus\nDEPLOYMENT_BOOTSTRAP_IDENTITY_KEY={s_firstKey}\n", Read(_envFile));
    }

    [Fact]
    public async Task ADatedCopyAlreadyThere_IsNeverOverwritten()
    {
        var copy = Harness.Write("state/.env-prod" + DatedSuffix, "an earlier copy\n");

        await SucceedAsync(Function());

        Assert.Equal("an earlier copy\n", Read(copy));
        Assert.Equal($"{Original}GIT_RUN_TOKEN_KEY={s_firstKey}\n", Read(_envFile));
    }

    [Fact]
    public async Task TheDatedCopy_KeepsTheModificationTimeOfTheFileItCopies()
    {
        // cp -p kept it, and Prepare host picks the latest installation backup by modification time.
        var written = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_envFile, written);

        await SucceedAsync(Function());

        Assert.Equal(written, File.GetLastWriteTimeUtc(_envFile + DatedSuffix));
    }

    [Fact]
    public async Task AFileWithoutAFinalNewline_GetsTheKeyOnALineOfItsOwn()
    {
        File.WriteAllText(_envFile, "APPNAME=aetheus");

        await SucceedAsync(Function());

        Assert.Equal($"APPNAME=aetheus\nGIT_RUN_TOKEN_KEY={s_firstKey}\n", Read(_envFile));
    }

    [PlatformFact("linux")]
    [SupportedOSPlatform("linux")]
    public async Task TheRewrittenFileAndItsDatedCopy_AreReadableByTheirOwnerOnly()
    {
        await SucceedAsync(Function());

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(mode, File.GetUnixFileMode(_envFile));
        Assert.Equal(mode, File.GetUnixFileMode(_envFile + DatedSuffix));
    }

    [Fact]
    public async Task TheRealRandomSource_DrawsSixtyFourLowercaseHexadecimalCharacters()
    {
        await SucceedAsync(new BackfillEnvSecretFunction());

        Assert.Matches(new Regex(@"(?m)^GIT_RUN_TOKEN_KEY=[0-9a-f]{64}$"), Read(_envFile));
    }

    [Theory]
    [InlineData("git_run_token_key")]
    [InlineData("1KEY")]
    [InlineData("KEY-A")]
    public async Task ANameThatIsNotAnEnvironmentKey_IsRefused(string key)
    {
        Parameters["key"] = key;

        Assert.Equal($"Not an environment key: '{key}'", await FailureAsync(Function()));
        Assert.Equal(Original, Read(_envFile));
    }

    [Fact]
    public async Task AMissingEnvironmentFile_IsRefusedByName()
    {
        File.Delete(_envFile);

        Assert.Equal($"{_envFile} is missing.", await FailureAsync(Function()));
    }

    [Fact]
    public async Task NoEnvironmentFile_IsRefusedByName()
    {
        Parameters.Remove("env_file");

        Assert.Equal("ENV_FILE is required", await FailureAsync(Function()));
    }

    [Fact]
    public async Task TheEnvironmentFileDefaultsToTheEnvFileVariable()
    {
        Parameters.Remove("env_file");
        Env["ENV_FILE"] = _envFile;

        await SucceedAsync(Function());

        Assert.Contains($"GIT_RUN_TOKEN_KEY={s_firstKey}", Read(_envFile), StringComparison.Ordinal);
    }

    [PlatformFact("linux")]
    [SupportedOSPlatform("linux")]
    public async Task ASymbolicLinkEnvironmentFile_IsRefused()
    {
        var link = PathIn("state/.env-link");
        File.CreateSymbolicLink(link, _envFile);
        Parameters["env_file"] = link;

        Assert.Equal("Production environment file must not be a symbolic link.", await FailureAsync(Function()));
        Assert.Equal(Original, Read(_envFile));
    }
}
