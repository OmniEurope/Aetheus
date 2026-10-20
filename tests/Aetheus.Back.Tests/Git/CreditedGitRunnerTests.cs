// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Git;

public sealed class CreditedGitRunnerTests : IDisposable
{
    // Private credential-file directory: the leak assertion observes only this runner, never the shared %TEMP%.
    private readonly string _credentialRoot =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"aetheus-credgit-{Guid.NewGuid():N}")).FullName;

    private readonly CreditedGitRunner _runner;

    public CreditedGitRunnerTests()
    {
        _runner = new CreditedGitRunner(NullLogger<CreditedGitRunner>.Instance, _credentialRoot);
    }

    public void Dispose() => Directory.Delete(_credentialRoot, recursive: true);

    [Fact]
    public async Task RunAsync_NoCredential_ExecutesGitAndCapturesChannels()
    {
        var result = await _runner.RunAsync(
            Path.GetTempPath(), ["--version"], null, null, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git version", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task RunAsync_HttpsCredential_MasksEphemeralAuthorizationHeader()
    {
        const string token = "secret-token-that-must-not-leak";
        var credential = new GitCredentialPayload
        {
            AuthType = GitAuthType.HttpsToken,
            Username = "operator",
            Token = token
        };

        var result = await _runner.RunAsync(
            Path.GetTempPath(), ["config", "--get-all", "http.extraHeader"], "https://example.invalid/repo.git",
            credential, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.DoesNotContain(token, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.Error, StringComparison.Ordinal);
        Assert.Contains("Authorization: Basic ***", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SshCredential_ShredsAllEphemeralCredentialFiles()
    {
        var credential = new GitCredentialPayload
        {
            AuthType = GitAuthType.Ssh,
            PrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nnot-a-real-key\n-----END PRIVATE KEY-----",
            KnownHosts = "example.invalid ssh-ed25519 AAAATEST",
            Passphrase = "do-not-log"
        };

        var result = await _runner.RunAsync(
            Path.GetTempPath(), ["--version"], "git@example.invalid:team/repo.git",
            credential, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // The runner writes its credential files only under the private root, so any entry left is a leak.
        var leaked = Directory.GetFileSystemEntries(_credentialRoot);
        Assert.True(leaked.Length == 0, $"Temporary credential files leaked: {string.Join(", ", leaked)}");
    }

    [Fact]
    public async Task RunAsync_InternalTimeout_KillsChildAndReturnsTimeoutResult()
    {
        var alias = OperatingSystem.IsWindows()
            ? "alias.aetheus-wait=!ping 127.0.0.1 -n 6 >NUL"
            : "alias.aetheus-wait=!sleep 5";

        var result = await _runner.RunAsync(
            Path.GetTempPath(), ["-c", alias, "aetheus-wait"], null, null,
            TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(50));

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("Timeout", result.Error);
    }
}
