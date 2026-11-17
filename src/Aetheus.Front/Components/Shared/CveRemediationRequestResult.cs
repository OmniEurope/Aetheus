// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>The answer to a request to prepare the fix of a CVE watch match: the attempt, and whether the platform could
/// not do it itself (HTTP 422), or whether one was already under way (HTTP 200), in which case <see cref="CveRemediationDto.Fallback"/> carries the command and the reason.</summary>
public sealed record CveRemediationRequestResult(CveRemediationDto Remediation, bool CouldNotPrepare, bool AlreadyActive);
