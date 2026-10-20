// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Services;

/// <summary>Stored integration settings are shown masked, and a masked value sent back by the editor
/// must restore the stored secret instead of overwriting it with the mask.</summary>
public sealed class SensitiveConfigurationJsonTests
{
    [Fact]
    public void MaskSecrets_HidesSensitiveKeysAtEveryDepthAndInsideArrays()
    {
        const string json = """{"name":"hook","token":"abc","nested":{"Password":"p","keep":1},"items":[{"secret":"s","label":"x"}]}""";

        var masked = JsonNode.Parse(SensitiveConfigurationJson.MaskSecrets(json))!;

        Assert.Equal("hook", (string?)masked["name"]);
        Assert.Equal("***", (string?)masked["token"]);
        Assert.Equal("***", (string?)masked["nested"]!["Password"]);
        Assert.Equal(1, (int?)masked["nested"]!["keep"]);
        Assert.Equal("***", (string?)masked["items"]![0]!["secret"]);
        Assert.Equal("x", (string?)masked["items"]![0]!["label"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public void MaskSecrets_UnreadableConfiguration_YieldsAnEmptyObject(string json) =>
        Assert.Equal("{}", SensitiveConfigurationJson.MaskSecrets(json));

    [Fact]
    public void RestoreMaskedSecrets_PutsTheStoredSecretBackWhereTheMaskCameBack()
    {
        const string existing = """{"url":"https://hook.example/x","token":"real","label":"old"}""";
        const string requested = """{"url":"***","token":"changed","label":"new"}""";

        var restored = JsonNode.Parse(SensitiveConfigurationJson.RestoreMaskedSecrets(existing, requested))!;

        Assert.Equal("https://hook.example/x", (string?)restored["url"]);
        Assert.Equal("changed", (string?)restored["token"]);
        Assert.Equal("new", (string?)restored["label"]);
    }

    [Fact]
    public void RestoreMaskedSecrets_FollowsArraysByPosition_AndLeavesExtraEntriesAlone()
    {
        const string existing = """{"targets":[{"secret":"one"},{"secret":"two"}]}""";
        const string requested = """{"targets":[{"secret":"***"},{"secret":"***"},{"secret":"***"}]}""";

        var restored = JsonNode.Parse(SensitiveConfigurationJson.RestoreMaskedSecrets(existing, requested))!;

        var targets = restored["targets"]!.AsArray();
        Assert.Equal("one", (string?)targets[0]!["secret"]);
        Assert.Equal("two", (string?)targets[1]!["secret"]);
        Assert.Equal("***", (string?)targets[2]!["secret"]);
    }

    [Fact]
    public void RestoreMaskedSecrets_MatchesKeysIgnoringCase_AndRecursesIntoObjects()
    {
        const string existing = """{"Auth":{"Password":"stored"}}""";
        const string requested = """{"auth":{"password":"***"}}""";

        var restored = JsonNode.Parse(SensitiveConfigurationJson.RestoreMaskedSecrets(existing, requested))!;

        Assert.Equal("stored", (string?)restored["auth"]!["password"]);
    }

    [Theory]
    [InlineData("not json", """{"token":"***"}""")]
    [InlineData("""{"token":"x"}""", "not json")]
    public void RestoreMaskedSecrets_UnreadableInput_ReturnsTheRequestUntouched(string existing, string requested) =>
        Assert.Equal(requested, SensitiveConfigurationJson.RestoreMaskedSecrets(existing, requested));
}
