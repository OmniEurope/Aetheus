// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Telemetry.Tests;

/// <summary>The tests that set <c>AETHEUS_TELEMETRY_ENABLED</c> share one collection: an environment variable
/// belongs to the whole process, so two classes setting it in parallel make each other read the other's value
/// (the "disabled" test then ran enabled and failed on the application name, 2026-11-17).</summary>
[CollectionDefinition(Name)]
public sealed class TelemetryEnvironmentCollection
{
    public const string Name = "AetheusTelemetryEnvironment";
}
