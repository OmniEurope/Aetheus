// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>After enrollment the one-shot registration token is blanked in appsettings.json; any
/// file that does not hold a non-empty token is left untouched.</summary>
public sealed class EnrollmentRegistrationTokenScrubTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aetheus-enroll-scrub-");

    private string SettingsPath => Path.Combine(_root.FullName, "appsettings.json");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void ClearRegistrationToken_BlanksTheTokenAndKeepsEverySiblingSetting()
    {
        File.WriteAllText(SettingsPath, """
            {
              // comments are tolerated in agent settings
              "Aetheus": { "ServerUrl": "https://backend.test", "RegistrationToken": "one-shot-secret" },
              "Logging": { "LogLevel": { "Default": "Information" } }
            }
            """);

        EnrollmentService.ClearRegistrationTokenInConfig(SettingsPath, NullLogger.Instance);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(string.Empty, root["Aetheus"]!["RegistrationToken"]!.GetValue<string>());
        Assert.Equal("https://backend.test", root["Aetheus"]!["ServerUrl"]!.GetValue<string>());
        Assert.Equal("Information", root["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
        Assert.DoesNotContain("one-shot-secret", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Theory]
    [InlineData("""{ "Aetheus": { "RegistrationToken": "" } }""")]
    [InlineData("""{ "Aetheus": { "RegistrationToken": null } }""")]
    [InlineData("""{ "Aetheus": { "RegistrationToken": 12345 } }""")]
    [InlineData("""{ "Aetheus": { "RegistrationToken": { "nested": "x" } } }""")]
    [InlineData("""{ "Aetheus": { "ServerUrl": "https://backend.test" } }""")]
    [InlineData("""{ "Aetheus": "flat" }""")]
    [InlineData("""{ "Logging": {} }""")]
    [InlineData("""[ "not", "an", "object" ]""")]
    [InlineData("""{ "Aetheus": { "RegistrationToken": "x" """)]
    public void ClearRegistrationToken_LeavesAnyOtherFileUntouched(string content)
    {
        File.WriteAllText(SettingsPath, content);

        EnrollmentService.ClearRegistrationTokenInConfig(SettingsPath, NullLogger.Instance);

        Assert.Equal(content, File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void ClearRegistrationToken_MissingFile_CreatesNothing()
    {
        EnrollmentService.ClearRegistrationTokenInConfig(SettingsPath, NullLogger.Instance);

        Assert.False(File.Exists(SettingsPath));
    }
}
