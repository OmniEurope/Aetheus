// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Tests.Architecture;

namespace Aetheus.Back.Tests;

public class VaultSecretGeneratorTests
{
    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData("JWT_KEY")]
    [InlineData("ENCRYPTION_KEY")]
    [InlineData("ENCRYPTION_SALT")]
    public void ProfileFor_RecognisesTheWellKnownKeys(string key)
    {
        Assert.True(VaultSecretGenerator.IsKnownKey(key));
        Assert.Equal(key, VaultSecretGenerator.ProfileFor(key).Key);
    }

    [Theory]
    [InlineData("db_password")]
    [InlineData("  JWT_KEY  ")]
    public void ProfileFor_IgnoresCaseAndSurroundingWhitespace(string key)
    {
        Assert.True(VaultSecretGenerator.IsKnownKey(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SOME_OTHER_SECRET")]
    public void ProfileFor_FallsBackToGenericForAnythingElse(string? key)
    {
        Assert.False(VaultSecretGenerator.IsKnownKey(key));
        Assert.Equal(VaultSecretGenerator.Generic, VaultSecretGenerator.ProfileFor(key));
    }

    // openssl rand -hex 24 produces 48 lowercase hex characters.
    [Fact]
    public void Generate_DbPassword_Matches48LowercaseHexCharacters()
    {
        var value = VaultSecretGenerator.Generate("DB_PASSWORD");
        Assert.Equal(48, value.Length);
        Assert.Matches("^[0-9a-f]{48}$", value);
    }

    // head -c 48 /dev/urandom | base64 | tr -d "/+=" strips the three characters, so the
    // length is at most the 64 base64 characters and the alphabet is strictly alphanumeric.
    [Theory]
    [InlineData("JWT_KEY")]
    [InlineData("ENCRYPTION_KEY")]
    public void Generate_StrippedBase64Keys_AreAlphanumericAndLongEnough(string key)
    {
        var value = VaultSecretGenerator.Generate(key);
        Assert.Matches("^[0-9A-Za-z]+$", value);
        Assert.InRange(value.Length, 32, 64);
    }

    // The demo and QA prepare steps (aetheus-demo.yaml, aetheus-nightly.yaml) refuse an ENCRYPTION_KEY shorter than 32 characters, and stripping
    // is random-dependent, so the floor has to hold for every draw, not just a lucky one.
    [Fact]
    public void Generate_EncryptionKey_AlwaysClearsTheThirtyTwoCharacterFloor()
    {
        for (var attempt = 0; attempt < 500; attempt++)
            Assert.True(VaultSecretGenerator.Generate("ENCRYPTION_KEY").Length >= 32);
    }

    // head -c 24 /dev/urandom | base64 keeps standard base64, padding included.
    [Fact]
    public void Generate_EncryptionSalt_IsStandardBase64Of24Bytes()
    {
        var value = VaultSecretGenerator.Generate("ENCRYPTION_SALT");
        Assert.Equal(32, value.Length);
        Assert.Equal(24, Convert.FromBase64String(value).Length);
    }

    [Fact]
    public void Generate_UnknownKey_StillProducesAStrongValue()
    {
        var value = VaultSecretGenerator.Generate("SOME_OTHER_SECRET");
        Assert.Matches("^[0-9A-Za-z]+$", value);
        Assert.InRange(value.Length, 32, 64);
    }

    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData("JWT_KEY")]
    [InlineData("ENCRYPTION_KEY")]
    [InlineData("ENCRYPTION_SALT")]
    [InlineData("SOME_OTHER_SECRET")]
    public void Generate_NeverRepeatsItself(string key)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 200; attempt++)
            Assert.True(values.Add(VaultSecretGenerator.Generate(key)), "Generated a duplicate value.");
    }

    [Fact]
    public void Profiles_ExposeTheFourDeploymentSecrets()
    {
        Assert.Equal(
            ["DB_PASSWORD", "JWT_KEY", "ENCRYPTION_KEY", "ENCRYPTION_SALT"],
            VaultSecretGenerator.Profiles.Select(profile => profile.Key));
    }

    // The recipes exist to match the deployment scripts; if a script changes, this test is the
    // tripwire that says the generator drifted away from the host it is supposed to imitate.
    [Fact]
    public void Recipes_StillMatchTheDeploymentScripts()
    {
        var repositoryRoot = RepositoryScan.Root;
        var deployScript = File.ReadAllText(Path.Combine(repositoryRoot, "deploy", "remote-deploy", "deploy.sh"));
        // The production recipe is declared by the versioned template; the renderer that runs it is
        // the heredoc of the prepare step in .pipeline/aetheus-deploy-prod.yaml: #{SECRET_HEX_<N>}# is
        // `openssl rand -hex <N>`.
        var productionTemplate = File.ReadAllText(
            Path.Combine(repositoryRoot, "deploy", "env", "prod.env.sample"));

        Assert.Contains("ensure_env_secret JWT_KEY", deployScript, StringComparison.Ordinal);
        Assert.Contains("ensure_env_secret ENCRYPTION_KEY", deployScript, StringComparison.Ordinal);
        Assert.Contains("ensure_env_secret ENCRYPTION_SALT", deployScript, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"(?m)^DB_PASSWORD=#\{SECRET_HEX_24\}#$"), productionTemplate);

        // JWT_KEY and ENCRYPTION_KEY: 48 bytes, base64, tr -d "/+=".
        foreach (var key in new[] { "JWT_KEY", "ENCRYPTION_KEY" })
        {
            var profile = VaultSecretGenerator.ProfileFor(key);
            Assert.Equal(48, profile.RandomBytes);
            Assert.Equal(VaultSecretGenerator.Encoding.Base64UrlSafeStripped, profile.Encoding);
            Assert.Matches(
                new Regex($@"ensure_env_secret\s+{key}\s+'head -c 48 /dev/urandom \| base64 \| tr -d ""/\+=""'"),
                deployScript);
        }

        // ENCRYPTION_SALT: 24 bytes, plain base64.
        var salt = VaultSecretGenerator.ProfileFor("ENCRYPTION_SALT");
        Assert.Equal(24, salt.RandomBytes);
        Assert.Equal(VaultSecretGenerator.Encoding.Base64, salt.Encoding);
        Assert.Matches(
            new Regex(@"ensure_env_secret\s+ENCRYPTION_SALT\s+'head -c 24 /dev/urandom \| base64'"),
            deployScript);

        // DB_PASSWORD: 24 bytes rendered as hex.
        var dbPassword = VaultSecretGenerator.ProfileFor("DB_PASSWORD");
        Assert.Equal(24, dbPassword.RandomBytes);
        Assert.Equal(VaultSecretGenerator.Encoding.Hex, dbPassword.Encoding);
    }

    /// <summary>"32 characters" in the Generate menu is exactly that many letters and digits, a
    /// fresh value each time.</summary>
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void GenerateCharacters_ReturnsExactlyThatManyLettersAndDigits(int length)
    {
        var first = VaultSecretGenerator.GenerateCharacters(length);
        var second = VaultSecretGenerator.GenerateCharacters(length);

        Assert.Matches($"^[A-Za-z0-9]{{{length}}}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GenerateCharacters_RefusesAnEmptyLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultSecretGenerator.GenerateCharacters(0));
    }
}
