// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The deployed baseline is read from a delivery contract that carries a candidate version on
/// one line and a full 40-hex source commit; any other contract is refused before it can steer the
/// release range.</summary>
public sealed class PipelineArtifactRestorerDeliveryContractTests
{
    private static readonly string Sha = new('A', 40);

    [Fact]
    public void ValidContract_GivesTheTrimmedVersionTheLowercaseCommitAndTheContractHash()
    {
        var bytes = Encoding.UTF8.GetBytes($"{{\"candidateVersion\":\" 1.2.3 \",\"sourceSha\":\"{Sha}\"}}");

        var baseline = PipelineArtifactRestorer.ParseDeliveryContract(bytes, "N-1");

        Assert.Equal("1.2.3", baseline.CandidateVersion);
        Assert.Equal(new string('a', 40), baseline.SourceSha);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), baseline.ContractSha256);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"sourceSha\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    [InlineData("{\"candidateVersion\":1,\"sourceSha\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    [InlineData("{\"candidateVersion\":\"1.0\"}")]
    [InlineData("{\"candidateVersion\":\"  \",\"sourceSha\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    [InlineData("{\"candidateVersion\":\"1.0\\n2.0\",\"sourceSha\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    [InlineData("{\"candidateVersion\":\"1.0\",\"sourceSha\":\"abc123\"}")]
    [InlineData("{\"candidateVersion\":\"1.0\",\"sourceSha\":\"ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ\"}")]
    public void InvalidContract_IsRefused(string json)
    {
        var error = Assert.Throws<InvalidDataException>(
            () => PipelineArtifactRestorer.ParseDeliveryContract(Encoding.UTF8.GetBytes(json), "N-1"));

        Assert.Equal("The N-1 delivery contract has no valid candidateVersion or sourceSha.", error.Message);
    }

    [Fact]
    public void OverlongCandidateVersion_IsRefused()
    {
        var bytes = Encoding.UTF8.GetBytes($"{{\"candidateVersion\":\"{new string('9', 257)}\",\"sourceSha\":\"{Sha}\"}}");

        Assert.Throws<InvalidDataException>(() => PipelineArtifactRestorer.ParseDeliveryContract(bytes, "N-1"));
    }
}
