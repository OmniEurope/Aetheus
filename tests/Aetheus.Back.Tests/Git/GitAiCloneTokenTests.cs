// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Tests.Git;

/// <summary>
/// Recette R-464: the autonomous AI clone token follows the run clone token's key rules, so wiring a
/// dedicated <c>GitLight:RunTokenKey</c> cannot break the AI checkouts of either blue-green colour.
/// </summary>
public class GitAiCloneTokenTests
{
    private const string Fallback = "unit-test-encryption-key-32chars!";
    private const string Dedicated = "dedicated-run-token-key-32-chars!!";
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static IConfiguration Config(string? dedicated) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:EncryptionKey"] = Fallback,
                ["GitLight:RunTokenKey"] = dedicated
            })
            .Build();

    [Fact]
    public void A_token_minted_under_the_fallback_is_accepted_by_the_colour_with_a_dedicated_key()
    {
        var (user, pass) = GitAiCloneToken.Mint(Config(dedicated: null), definitionId: 7, projectId: 3, Now);

        Assert.Equal(7, GitAiCloneToken.Validate(Config(Dedicated), user, pass, projectId: 3, Now));
        Assert.Null(GitAiCloneToken.Validate(Config(Dedicated), user, pass, projectId: 4, Now));
    }

    [Fact]
    public void An_empty_dedicated_key_signs_with_the_fallback()
    {
        var (user, pass) = GitAiCloneToken.Mint(Config(dedicated: ""), definitionId: 7, projectId: 3, Now);

        Assert.Equal(7, GitAiCloneToken.Validate(Config(dedicated: null), user, pass, projectId: 3, Now));
    }

    [Fact]
    public void A_token_signed_by_an_unknown_secret_is_refused()
    {
        var (user, pass) = GitAiCloneToken.Mint(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:EncryptionKey"] = "another-secret-entirely-32-chars!" })
                .Build(),
            definitionId: 7, projectId: 3, Now);

        Assert.Null(GitAiCloneToken.Validate(Config(Dedicated), user, pass, projectId: 3, Now));
    }
}
