// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Functions;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the first-deploy block of the production preparation and of
/// <c>render-env-sample.sh</c>, with the proof <c>verify-prod-env-sample.sh</c> ran on them: the real
/// versioned template <c>deploy/env/prod.env.sample</c> renders byte for byte the reference environment
/// (14 keys, 6 generated secrets); template and produced keys match in membership and order; every
/// secret of the template is a marker, never a value; each malformed template is refused with a stated
/// reason; a render that fails leaves no environment behind. It also pins what
/// <c>VaultSecretGeneratorTests</c> used to read in the script: <c>#{SECRET_HEX_&lt;N&gt;}#</c> is N
/// random bytes written as 2N lowercase hexadecimal characters, the recipe of <c>openssl rand -hex N</c>.
/// </summary>
public sealed class RenderEnvSampleFunctionTests : ProductionFunctionTestBase
{
    private const string ApiUrl = "https://api.example.org";
    private const string AppUrl = "https://app.example.org";
    private const string Version = "1.1.1842";

    private readonly List<int> _draws = [];
    private readonly string _envFile;
    private readonly string _stateDirectory;

    public RenderEnvSampleFunctionTests()
        : base("render-env-sample")
    {
        _stateDirectory = Directory.CreateDirectory(Path.Combine(Root, "state")).FullName;
        _envFile = PathIn("state/.env-prod");
        Parameters["env_file"] = _envFile;
        Parameters["sample"] = "case.sample";
        Parameters["public_api_url"] = ApiUrl;
        Parameters["public_app_url"] = AppUrl;
        Parameters["app_version"] = Version;
    }

    /// <summary>Draw number k of <c>openssl rand -hex N</c> printed 2N copies of hexadecimal digit k, as
    /// the shell proof's stand-in did: the reference environment is built from the same draws.</summary>
    private string Draw(int bytes)
    {
        _draws.Add(bytes);
        return new string(Convert.ToHexStringLower([(byte)(_draws.Count % 16)])[1], bytes * 2);
    }

    private RenderEnvSampleFunction Function() => new(Draw);

    private void Sample(string content) => Harness.Write("ws/case.sample", content);

    /// <summary>The versioned production template, with the line endings of a Linux checkout.</summary>
    private static string Template() =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "deploy", "env", "prod.env.sample")).ReplaceLineEndings("\n");

    private string ReferenceEnvironment()
    {
        string Secret(int bytes, int call) => new(Convert.ToHexStringLower([(byte)call])[1], bytes * 2);
        return $"""
            APPNAME=aetheus
            ENV=prod
            API_BASE_URL={ApiUrl}
            APP_VERSION={Version}
            DB_USER=aetheus
            DB_PASSWORD={Secret(24, 1)}
            JWT_KEY={Secret(32, 2)}
            ADMIN_PASSWORD={Secret(16, 3)}
            ENCRYPTION_KEY={Secret(32, 4)}
            ENCRYPTION_SALT={Secret(16, 5)}
            DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY={Secret(32, 6)}
            FRONT_URL={AppUrl}
            FRONT_URL_ACCEPT={AppUrl}
            FRONT_URL_PROD={AppUrl}

            """.ReplaceLineEndings("\n");
    }

    [Fact]
    public async Task TheVersionedProductionTemplate_RendersTheReferenceEnvironmentByteForByte()
    {
        Sample(Template());

        await SucceedAsync(Function());

        Assert.Equal(ReferenceEnvironment(), Read(_envFile));
        Assert.Equal([24, 32, 16, 32, 16, 32], _draws);
        Assert.Contains($">>> First deploy: generating persistent secrets at {_envFile}", LogLines);
        Assert.DoesNotContain(Directory.GetFiles(_stateDirectory), path => path.Contains(".first-deploy.", StringComparison.Ordinal));
    }

    [PlatformFact("linux")]
    [SupportedOSPlatform("linux")]
    public async Task TheRenderedEnvironment_IsReadableByItsOwnerOnly()
    {
        Sample(Template());

        await SucceedAsync(Function());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_envFile));
    }

    [Fact]
    public async Task TemplateAndProducedKeys_MatchInMembershipAndOrder()
    {
        var template = Template();
        Sample(template);

        await SucceedAsync(Function());

        string[] Keys(string text) => text.Split('\n')
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line[..line.IndexOf('=', StringComparison.Ordinal)])
            .ToArray();
        Assert.Equal(Keys(template), Keys(Read(_envFile)));
        Assert.Equal(14, Keys(template).Length);
    }

    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData("JWT_KEY")]
    [InlineData("ADMIN_PASSWORD")]
    [InlineData("ENCRYPTION_KEY")]
    [InlineData("ENCRYPTION_SALT")]
    [InlineData("DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY")]
    public void EverySecretOfTheTemplate_IsAMarker_NeverAValue(string secret) =>
        Assert.Matches(new Regex($@"(?m)^{secret}=#\{{SECRET_HEX_[0-9]+\}}#$"), Template());

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(64)]
    public async Task ASecretMarker_IsThatManyRandomBytesWrittenAsTwiceThatManyLowercaseHexadecimalCharacters(int bytes)
    {
        Sample($"SECRET=#{{SECRET_HEX_{bytes}}}#\n");

        await SucceedAsync(new RenderEnvSampleFunction());

        Assert.Matches(new Regex($"^SECRET=[0-9a-f]{{{bytes * 2}}}\n$"), Read(_envFile));
    }

    [Fact]
    public async Task TheRealRandomSource_DrawsAFreshValueForEveryMarker()
    {
        Sample("A=#{SECRET_HEX_32}#\nB=#{SECRET_HEX_32}#\n");

        await SucceedAsync(new RenderEnvSampleFunction());

        var values = Read(_envFile).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line[2..]).ToArray();
        Assert.Equal(2, values.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("A=1\nB=#{DB_PASSWORD}#\n", "case.sample:2: undeclared placeholder in B.")]
    [InlineData("A=1\nA=2\n", "case.sample:2: key A is declared twice.")]
    [InlineData("A=#{SECRET_HEX_8}#\n", "case.sample:1: a secret shorter than 16 bytes is refused.")]
    [InlineData("A=#{SECRET_HEX_x}#\n", "case.sample:1: secret length 'x' is not a number.")]
    [InlineData("A=#{SECRET_HEX_}#\n", "case.sample:1: secret length '' is not a number.")]
    [InlineData("A=#{SECRET_HEX_2000}#\n", "case.sample:1: a secret longer than 1024 bytes is refused.")]
    [InlineData("NOT A KEY\n", "case.sample:1 is not KEY=VALUE.")]
    [InlineData("lower=1\n", "case.sample:1: key 'lower' must start with an upper-case letter.")]
    [InlineData("=1\n", "case.sample:1: key '' must start with an upper-case letter.")]
    [InlineData("A-B=1\n", "case.sample:1: key 'A-B' must be upper case, digits and underscores.")]
    [InlineData("A=\n", "case.sample:1 A has an empty value.")]
    [InlineData("A=1\r\n", "case.sample:1 carries a carriage return.")]
    [InlineData("# a comment\r\nA=1\n", "case.sample:1 carries a carriage return.")]
    [InlineData("\n# only a comment\n", "Environment template declares no key: case.sample")]
    [InlineData("A=x}#\n", "case.sample:1: undeclared placeholder in A.")]
    public async Task AMalformedTemplate_IsRefusedWithAStatedReason_AndNoEnvironmentIsWritten(string template, string reason)
    {
        Sample(template);

        var failure = await FailureAsync(Function());

        Assert.Equal($"Could not render {Path.Combine(Workspace, "case.sample")}; no production environment was written.", failure);
        Assert.Contains(Harness.Log, entry => entry.Level == TaskLogLevel.Error
            && entry.Line == "FATAL: " + reason.Replace("case.sample", Path.Combine(Workspace, "case.sample"), StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(_stateDirectory));
    }

    [Fact]
    public async Task AMissingTemplate_IsRefused_AndNoEnvironmentIsWritten()
    {
        var failure = await FailureAsync(Function());

        Assert.StartsWith("Could not render ", failure, StringComparison.Ordinal);
        Assert.Contains(Harness.Log, entry => entry.Line.StartsWith("FATAL: Environment template not found: ", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(_stateDirectory));
    }

    [Theory]
    [InlineData("public_api_url", "#{PUBLIC_API_URL}#", "PUBLIC_API_URL is required")]
    [InlineData("public_app_url", "#{PUBLIC_APP_URL}#", "PUBLIC_APP_URL is required")]
    [InlineData("app_version", "#{APP_VERSION}#", "APP_VERSION is required")]
    public async Task ARunValueTheTemplateNamesButTheRunLacks_IsRefused(string parameter, string placeholder, string reason)
    {
        Sample($"A={placeholder}\n");
        Parameters.Remove(parameter);

        await FailureAsync(Function());

        Assert.Contains(Harness.Log, entry => entry.Line == "FATAL: " + reason);
        Assert.Empty(Directory.GetFileSystemEntries(_stateDirectory));
    }

    [Fact]
    public async Task TheRunValues_DefaultToTheirVariables()
    {
        Sample("A=#{PUBLIC_API_URL}#\nB=#{PUBLIC_APP_URL}#\nC=#{APP_VERSION}#\n");
        Parameters.Remove("public_api_url");
        Parameters.Remove("public_app_url");
        Parameters.Remove("app_version");
        Env["PUBLIC_API_URL"] = ApiUrl;
        Env["PUBLIC_APP_URL"] = AppUrl;
        Env["APP_VERSION"] = Version;

        await SucceedAsync(Function());

        Assert.Equal($"A={ApiUrl}\nB={AppUrl}\nC={Version}\n", Read(_envFile));
    }

    [Theory]
    [InlineData("0123", "The random source produced a secret of the wrong length for A.")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", "The random source produced a non-hexadecimal secret for A.")]
    [InlineData("ABABABABABABABABABABABABABABABAB", "The random source produced a non-hexadecimal secret for A.")]
    public async Task ADrawOfTheWrongShape_IsRefused_AndNoEnvironmentIsWritten(string drawn, string reason)
    {
        Sample("A=#{SECRET_HEX_16}#\n");

        await FailureAsync(new RenderEnvSampleFunction(_ => drawn));

        Assert.Contains(Harness.Log, entry => entry.Line == "FATAL: " + reason);
        Assert.Empty(Directory.GetFileSystemEntries(_stateDirectory));
    }

    [Fact]
    public async Task AnExistingEnvironment_IsReusedVerbatim_AndNothingIsDrawn()
    {
        Sample(Template());
        File.WriteAllText(_envFile, "DB_PASSWORD=the-existing-one\n");

        await SucceedAsync(Function());

        Assert.Equal("DB_PASSWORD=the-existing-one\n", Read(_envFile));
        Assert.Empty(_draws);
        Assert.Contains($">>> Reusing existing secrets at {_envFile} (not regenerated).", LogLines);
        Assert.Equal([".env-prod"], Directory.GetFiles(_stateDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AnEnvironmentAnotherDeploymentCreatedMeanwhile_IsReused_NotOverwritten()
    {
        Sample("A=#{SECRET_HEX_16}#\n");
        var racing = new RenderEnvSampleFunction(bytes =>
        {
            File.WriteAllText(_envFile, "A=the-other-deployment\n");
            return new string('c', bytes * 2);
        });

        await SucceedAsync(racing);

        Assert.Equal("A=the-other-deployment\n", Read(_envFile));
        Assert.Contains($">>> Another deployment created {_envFile}; reusing it.", LogLines);
        Assert.Equal([".env-prod"], Directory.GetFiles(_stateDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AStateDirectoryThatDoesNotExist_IsRefused()
    {
        Sample("A=1\n");
        Parameters["env_file"] = PathIn("nowhere/.env-prod");

        Assert.Equal("Could not persist production secrets.", await FailureAsync(Function()));
    }

    [Fact]
    public async Task NoEnvironmentFile_IsRefusedByName()
    {
        Parameters.Remove("env_file");

        Assert.Equal("ENV_FILE is required", await FailureAsync(Function()));
    }

    [PlatformFact("linux")]
    [SupportedOSPlatform("linux")]
    public async Task ASymbolicLinkEnvironmentFile_IsRefused()
    {
        Sample("A=1\n");
        var target = Harness.Write("elsewhere/.env", "A=1\n");
        var link = PathIn("state/.env-link");
        File.CreateSymbolicLink(link, target);
        Parameters["env_file"] = link;

        Assert.Equal("Production environment file must not be a symbolic link.", await FailureAsync(Function()));
        Assert.Empty(_draws);
    }
}
