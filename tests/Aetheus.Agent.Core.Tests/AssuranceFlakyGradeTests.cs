// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>PLAN-006: the grade of a candidate's flaky tests, from their count over every sealed list.</summary>
public sealed class AssuranceFlakyGradeTests
{
    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(10, "B")]
    [InlineData(11, "C")]
    [InlineData(20, "C")]
    [InlineData(21, "F")]
    [InlineData(5000, "F")]
    public void Grade_FollowsTheOwnerBands(int count, string grade) =>
        Assert.Equal(grade, AssuranceFlakyGrade.Grade(count));

    [Fact]
    public void Count_NamesEachTestOnceAcrossSuites() =>
        Assert.Equal(3, AssuranceFlakyGrade.Count(["A, B", "B, C(id: 2; x: 3)", "A"]));

    [Fact]
    public void Count_AddsTheTestsACutListOnlyCounted() =>
        Assert.Equal(1 + 20 + 81, AssuranceFlakyGrade.Count(["Solo", string.Join(", ", Enumerable.Range(0, 20).Select(index => $"T{index}")) + ", +81 more"]));

    [Fact]
    public void Count_OfNoListIsZero() =>
        Assert.Equal(0, AssuranceFlakyGrade.Count([]));
}
