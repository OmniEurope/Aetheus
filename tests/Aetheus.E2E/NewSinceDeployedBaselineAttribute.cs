// SPDX-License-Identifier: EUPL-1.2
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace Aetheus.E2E;

/// <summary>Marks features unavailable in the preceding deployed baseline: skip only when
/// E2E_PREVIOUS_PAYLOAD_ONLY=true makes this suite run against baseline images without their own
/// runtime. Remove the marker once the deployed baseline includes the feature so validation resumes
/// there.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NewSinceDeployedBaselineAttribute(string recetteId) : NUnitAttribute, IApplyToTest
{
    internal static readonly bool TargetIsPayloadOnlyPrevious =
        bool.TryParse(Environment.GetEnvironmentVariable("E2E_PREVIOUS_PAYLOAD_ONLY"), out var value) && value;

    public string RecetteId { get; } = recetteId;

    public void ApplyToTest(Test test)
    {
        if (!TargetIsPayloadOnlyPrevious || test.RunState == RunState.NotRunnable) return;
        test.RunState = RunState.Ignored;
        test.Properties.Set(PropertyNames.SkipReason,
            $"{RecetteId} is newer than the deployed baseline, and this run targets that baseline's images.");
    }
}
