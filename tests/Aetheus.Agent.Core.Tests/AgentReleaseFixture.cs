// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aetheus.Agent.Core.Tests;

/// <summary>An agent release directory as the backend image serves it: a manifest naming a commit,
/// its protocols and capabilities, and one Linux x64 archive with its size and SHA-256.</summary>
internal static class AgentReleaseFixture
{
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";
    public const string ArchiveName = "aetheus-agent-linux-x64.tar.gz";
    public static readonly byte[] ArchiveBytes = Encoding.UTF8.GetBytes("fake agent archive");

    public static JsonObject Manifest() => new()
    {
        ["commit"] = Commit,
        ["protocolVersion"] = 2,
        ["minimumSupportedProtocol"] = 1,
        ["maximumSupportedProtocol"] = 2,
        ["softwareCapabilities"] = new JsonArray("ai.run", "shell"),
        ["archives"] = new JsonArray(new JsonObject
        {
            ["fileName"] = ArchiveName,
            ["platform"] = "linux",
            ["architecture"] = "x64",
            ["sizeBytes"] = ArchiveBytes.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(ArchiveBytes))
        })
    };

    public static void Write(string directory, JsonObject manifest)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "agent-release-manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllBytes(Path.Combine(directory, ArchiveName), ArchiveBytes);
    }
}
