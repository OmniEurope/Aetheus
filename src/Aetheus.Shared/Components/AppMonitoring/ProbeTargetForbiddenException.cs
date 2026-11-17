// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary>
/// Thrown by the SSRF guard of the probe client when a target resolves to a private or loopback address.
/// A typed signal, so the failure is classified by type and never by its message (R-29).
/// </summary>
public sealed class ProbeTargetForbiddenException() : HttpRequestException(
    "Probe target resolved to a forbidden (private/loopback) address.");
