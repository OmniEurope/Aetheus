// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.ExternalRepos;
using Aetheus.Back.Components.Git;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Tests.Git;

public class GitRunCloneTokenTests
{
    private static IConfiguration Config(string key = "unit-test-encryption-key-32chars!") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:EncryptionKey"] = key })
            .Build();

    private static readonly DateTime Now = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Mint_then_validate_roundtrips_for_same_run_and_project()
    {
        var config = Config();
        var (user, pass) = GitRunCloneToken.Mint(config, runId: 39, projectId: 1, Now, GitRunCloneToken.DefaultTtl);

        Assert.StartsWith(GitRunCloneToken.UsernamePrefix, user);
        Assert.Equal(39, GitRunCloneToken.Validate(config, user, pass, projectId: 1, Now));
    }

    // During a blue-green switch to a dedicated RunTokenKey, the colour that has it must still
    // accept the clone tokens the other colour minted under the Auth:EncryptionKey fallback.
    [Fact]
    public void Validate_accepts_a_token_minted_under_the_fallback_key_once_a_dedicated_key_is_configured()
    {
        var oldColour = Config("unit-test-encryption-key-32chars!");
        var newColour = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:EncryptionKey"] = "unit-test-encryption-key-32chars!",
                ["GitLight:RunTokenKey"] = "dedicated-run-token-key-32-chars!!"
            })
            .Build();
        var (oldUser, oldPass) = GitRunCloneToken.Mint(oldColour, runId: 39, projectId: 1, Now, GitRunCloneToken.DefaultTtl);
        var (newUser, newPass) = GitRunCloneToken.Mint(newColour, runId: 40, projectId: 1, Now, GitRunCloneToken.DefaultTtl);

        Assert.Equal(39, GitRunCloneToken.Validate(newColour, oldUser, oldPass, projectId: 1, Now));
        Assert.Equal(40, GitRunCloneToken.Validate(newColour, newUser, newPass, projectId: 1, Now));
        Assert.Null(GitRunCloneToken.Validate(Config("another-secret-entirely-32-chars!"), newUser, newPass, projectId: 1, Now));
    }

    // Compose writes an unset ${GIT_RUN_TOKEN_KEY:-} as an empty string. An empty dedicated key is
    // no key: tokens are signed with the fallback, not refused with "no signing key".
    [Fact]
    public void An_empty_dedicated_key_signs_with_the_fallback_and_the_fallback_colour_accepts_it()
    {
        var emptyDedicated = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:EncryptionKey"] = "unit-test-encryption-key-32chars!",
                ["GitLight:RunTokenKey"] = ""
            })
            .Build();

        var (user, pass) = GitRunCloneToken.Mint(emptyDedicated, runId: 41, projectId: 1, Now, GitRunCloneToken.DefaultTtl);

        Assert.Equal(41, GitRunCloneToken.Validate(Config(), user, pass, projectId: 1, Now));
    }

    [Fact]
    public void Validate_rejects_every_token_when_no_secret_is_configured()
    {
        var (user, pass) = GitRunCloneToken.Mint(Config(), runId: 39, projectId: 1, Now, GitRunCloneToken.DefaultTtl);

        Assert.Null(GitRunCloneToken.Validate(new ConfigurationBuilder().Build(), user, pass, projectId: 1, Now));
    }

    [Fact]
    public void Validate_rejects_a_different_project()
    {
        var config = Config();
        var (user, pass) = GitRunCloneToken.Mint(config, runId: 39, projectId: 1, Now, GitRunCloneToken.DefaultTtl);

        Assert.Null(GitRunCloneToken.Validate(config, user, pass, projectId: 2, Now));
    }

    [Fact]
    public void Validate_rejects_an_expired_token()
    {
        var config = Config();
        var (user, pass) = GitRunCloneToken.Mint(config, runId: 39, projectId: 1, Now, TimeSpan.FromHours(1));

        Assert.Null(GitRunCloneToken.Validate(config, user, pass, projectId: 1, Now.AddHours(2)));
    }

    [Fact]
    public void Validate_rejects_a_token_signed_with_a_different_key()
    {
        var (user, pass) = GitRunCloneToken.Mint(Config("key-a-32-chars-padding-padding!!"), 39, 1, Now, GitRunCloneToken.DefaultTtl);

        Assert.Null(GitRunCloneToken.Validate(Config("key-b-32-chars-padding-padding!!"), user, pass, 1, Now));
    }

    [Fact]
    public void Validate_rejects_a_tampered_payload()
    {
        var config = Config();
        var (user, pass) = GitRunCloneToken.Mint(config, 39, 1, Now, GitRunCloneToken.DefaultTtl);
        var tampered = pass[..^2] + (pass[^1] == 'a' ? "bb" : "aa");

        Assert.Null(GitRunCloneToken.Validate(config, user, tampered, 1, Now));
    }

    [Fact]
    public void Validate_rejects_a_non_run_username()
    {
        var config = Config();
        Assert.Null(GitRunCloneToken.Validate(config, "alice", "whatever", 1, Now));
    }

    [Theory]
    [InlineData("http://localhost:5300/git/1/toto.git", "http://host.docker.internal:5300/git/1/toto.git")]
    [InlineData("https://old-domain.example/git/42/app.git", "http://host.docker.internal:5300/git/42/app.git")]
    public void Rehome_swaps_only_the_authority_of_a_mirror_url(string stored, string expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitLight:CloneBaseUrl"] = "http://host.docker.internal:5300" })
            .Build();

        Assert.Equal(expected, MirrorCloneUrl.Rehome(config, stored));
    }

    [Theory]
    [InlineData("https://github.com/acme/app.git")]
    // Same /git/x/y.git shape but a NON-numeric middle segment - not one of our mirror paths (the
    // projectId is always an int), so it must be left untouched rather than re-homed.
    [InlineData("https://github.com/git/acme/app.git")]
    [InlineData("https://example.test/git/0/app.git")]
    public void Rehome_leaves_a_non_mirror_url_unchanged(string stored)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitLight:CloneBaseUrl"] = "http://host.docker.internal:5300" })
            .Build();

        Assert.Equal(stored, MirrorCloneUrl.Rehome(config, stored));
    }
}
