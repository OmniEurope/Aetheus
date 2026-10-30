// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Tests.Shared;

/// <summary>
/// SHARED-002: a legacy body is a flat dictionary of parameters, and a pipeline may legally name a
/// parameter <c>parameters</c>, <c>sourceBranch</c> or <c>idempotencyKey</c>. Recognizing the current
/// shape by one of those names alone refused such a body, or turned the parameter into a branch
/// override and dropped the others.
/// </summary>
public sealed class PipelineRunRequestJsonConverterTests
{
    private static PipelineRunRequest Read(string json) =>
        JsonSerializer.Deserialize<PipelineRunRequest>(json)!;

    [Fact]
    public void LegacyParameterNamedParameters_IsReadAsAParameter()
    {
        var request = Read("""{"parameters":"x"}""");

        Assert.Equal("x", request.Parameters!["parameters"]);
        Assert.Null(request.SourceBranch);
    }

    [Fact]
    public void LegacyParameterNamedSourceBranch_BesideAnother_KeepsBothAsParameters()
    {
        var request = Read("""{"sourceBranch":"x","mode":"prod"}""");

        Assert.Equal("x", request.Parameters!["sourceBranch"]);
        Assert.Equal("prod", request.Parameters["mode"]);
        Assert.Null(request.SourceBranch);
    }

    [Fact]
    public void LegacyParameterNamedIdempotencyKey_BesideAnother_KeepsBothAsParameters()
    {
        var request = Read("""{"idempotencyKey":"k","mode":"prod","AETHEUS_RUN_BRANCH":"release/1"}""");

        Assert.Equal("k", request.Parameters!["idempotencyKey"]);
        Assert.Equal("prod", request.Parameters["mode"]);
        Assert.Null(request.IdempotencyKey);
        Assert.Equal("release/1", request.SourceBranch);
    }

    [Fact]
    public void OrdinaryLegacyDictionary_IsStillReadAsParameters()
    {
        var request = Read("""{"mode":"prod","region":"eu"}""");

        Assert.Equal(2, request.Parameters!.Count);
        Assert.Equal("prod", request.Parameters["mode"]);
    }

    [Fact]
    public void CurrentShape_WithNullParametersAndBranch_IsTheCurrentShape()
    {
        var request = Read("""{"parameters":null,"sourceBranch":"release/1","idempotencyKey":"k"}""");

        Assert.Null(request.Parameters);
        Assert.Equal("release/1", request.SourceBranch);
        Assert.Equal("k", request.IdempotencyKey);
    }

    [Fact]
    public void BranchOnlyBody_IsTheAmbiguousCase_ReadAsTheCurrentShape()
    {
        var request = Read("""{"sourceBranch":"release/1"}""");

        Assert.Null(request.Parameters);
        Assert.Equal("release/1", request.SourceBranch);
    }

    [Fact]
    public void CurrentShape_RoundTripsThroughTheWriter()
    {
        var original = new PipelineRunRequest
        {
            Parameters = new Dictionary<string, string> { ["sourceBranch"] = "a", ["parameters"] = "b" },
            SourceBranch = "release/1",
            IdempotencyKey = "k"
        };

        var request = Read(JsonSerializer.Serialize(original));

        Assert.Equal("a", request.Parameters!["sourceBranch"]);
        Assert.Equal("b", request.Parameters["parameters"]);
        Assert.Equal("release/1", request.SourceBranch);
        Assert.Equal("k", request.IdempotencyKey);
    }

    /// <summary>R-72: the override travels in both shapes, the legacy one the front sends and the current one.</summary>
    [Fact]
    public void GateOverride_IsReadFromTheLegacyAndTheCurrentShape_AndRoundTrips()
    {
        var legacy = Read("""{"candidateVersion":"1.2.3","AETHEUS_RUN_GATE_OVERRIDE":"true"}""");
        Assert.True(legacy.GateOverride);
        Assert.Equal("1.2.3", Assert.Single(legacy.Parameters!).Value);

        Assert.False(Read("""{"candidateVersion":"1.2.3","AETHEUS_RUN_GATE_OVERRIDE":"yes"}""").GateOverride);
        Assert.True(Read("""{"parameters":{"candidateVersion":"1.2.3"},"gateOverride":true}""").GateOverride);
        Assert.False(Read("""{"parameters":{"candidateVersion":"1.2.3"}}""").GateOverride);

        var roundTrip = Read(JsonSerializer.Serialize(new PipelineRunRequest { GateOverride = true }));
        Assert.True(roundTrip.GateOverride);
    }
}
